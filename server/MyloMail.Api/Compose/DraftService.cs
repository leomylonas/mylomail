using Microsoft.EntityFrameworkCore;
using MyloMail.Api.Domain;
using MyloMail.Api.Hubs;
using MyloMail.Api.Outbox;
using MyloMail.Api.Persistence;
using MyloMail.Api.Providers;
using MyloMail.Api.Providers.Contracts;

namespace MyloMail.Api.Compose;

/// <summary>What the compose window sends when it saves.</summary>
public sealed record DraftInput(
	Guid? DraftId,
	Guid AccountId,
	Guid? SendIdentityId,
	Guid? InReplyToMessageId,
	IReadOnlyList<Address> To,
	IReadOnlyList<Address> Cc,
	IReadOnlyList<Address> Bcc,
	string Subject,
	string BodyHtml
);

/// <summary>
/// Local draft authoring, and handing a finished draft to the outbox (§1, §15).
/// </summary>
public sealed class DraftService(
	MyloMailDbContext context,
	OutboxService outbox,
	DraftSyncService remote,
	IMailProviderFactory providers,
	IDraftDispatcher dispatcher,
	TimeProvider clock,
	IHubEvents events
)
{
	/// <summary>
	/// Creates or updates a draft.
	/// </summary>
	/// <remarks>
	/// A draft stays structured rather than becoming MIME on every save: it is a mutable
	/// authoring document, and treating each keystroke as a mutation of a MIME message would
	/// be perverse and would make conflict handling far worse (§1). MIME is generated once,
	/// at send.
	/// </remarks>
	public async Task<Draft> SaveAsync(DraftInput input, CancellationToken ct = default)
	{
		var draft = input.DraftId is Guid id
			? await context.Drafts.FirstOrDefaultAsync(d => d.Id == id, ct)
			: null;

		if (draft is null)
		{
			draft = new Draft { Id = input.DraftId ?? Guid.NewGuid(), AccountId = input.AccountId };
			context.Drafts.Add(draft);
		}

		// The caller round-trips whatever identity a previous save reported, the same way
		// InReplyToMessageId is round-tripped — only a brand-new draft that has never had one
		// chosen falls back to the account's default (§15).
		draft.SendIdentityId = input.SendIdentityId ?? await DefaultIdentityAsync(input.AccountId, ct);
		draft.InReplyToMessageId = input.InReplyToMessageId;
		draft.To = input.To;
		draft.Cc = input.Cc;
		draft.Bcc = input.Bcc;
		draft.Subject = input.Subject;
		draft.BodyHtml = input.BodyHtml;
		draft.SavedAt = clock.GetUtcNow();

		await context.SaveChangesAsync(ct);

		// After the commit, so the push sends what was stored rather than racing it.
		dispatcher.RequestPush(draft.AccountId);

		await events.DraftUpdatedAsync(draft.Id);
		return draft;
	}

	/// <summary>
	/// Resolves a draft flagged <see cref="Draft.SyncConflict"/> — the server's copy changed
	/// since this one was read, and both are kept until the user chooses (§1, §15).
	/// </summary>
	/// <param name="keepMine">
	/// True force-keeps the local version; false discards the local edit and pulls the
	/// server's actual current content instead — mirrors <c>CalendarEventService.ResolveConflictAsync</c>'s
	/// shape, diverging only where a draft's actual mechanics require it.
	/// </param>
	/// <remarks>
	/// "Keep mine" cannot simply retry the push with no expected revision: every provider's
	/// <c>CreateOrUpdateDraftAsync</c> treats a null revision as "create," not "force-update"
	/// — passing one against an existing <see cref="Draft.ProviderDraftId"/> would create a
	/// second, orphaned draft rather than overwrite the conflicting one. Abandoning the old
	/// remote draft and letting the next ordinary push create a fresh one from local content
	/// is the only way to make the local version win without ending up with two.
	/// </remarks>
	public async Task<Draft> ResolveConflictAsync(Guid draftId, bool keepMine, CancellationToken ct = default)
	{
		var draft = await context.Drafts.FirstAsync(d => d.Id == draftId, ct);
		if (!draft.SyncConflict)
		{
			return draft;
		}

		if (keepMine)
		{
			if (draft.ProviderDraftId is string remoteId)
			{
				await remote.RemoveRemoteAsync(draft.AccountId, remoteId, ct);
			}
			draft.ProviderDraftId = null;
			draft.ProviderRevision = null;
			draft.PushedAt = null;
			draft.SyncConflict = false;
			await context.SaveChangesAsync(ct);
			dispatcher.RequestPush(draft.AccountId);
		}
		else if (draft.ProviderDraftId is string providerDraftId)
		{
			var account = await context.Accounts.FirstAsync(a => a.Id == draft.AccountId, ct);
			// Deliberately not FirstAsync: nothing enforces that at most one mailbox per
			// account has effective SpecialUse.Drafts (a manual override — §13 Epic 2 — has
			// no uniqueness check against other mailboxes already holding that use). Picking
			// an arbitrary one of several candidates would address the raw-message fetch
			// below against the wrong mailbox's id-space, silently materialising a different
			// message's content into this draft. Failing loudly here is safer than that.
			var draftsMailboxes = await context
				.Mailboxes.Where(m => m.AccountId == draft.AccountId && (m.SpecialUseOverride ?? m.SpecialUse) == SpecialUse.Drafts)
				.ToListAsync(ct);
			if (draftsMailboxes.Count != 1)
			{
				throw new InvalidOperationException(
					$"Expected exactly one Drafts mailbox for account {draft.AccountId}, found {draftsMailboxes.Count}."
				);
			}
			var draftsMailbox = draftsMailboxes[0];
			var raw = await providers
				.For(account)
				.FetchRawMessageAsync(
					account,
					new MessageOccurrenceRef(Guid.Empty, draftsMailbox.Id, providerDraftId),
					ct
				);
			// The stored ProviderRevision is carried through unchanged rather than refreshed:
			// FetchRawMessageAsync's contract is content-only, with no provider-agnostic way to
			// also read the current revision without a second, targeted request no provider
			// here exposes. Known consequence: it is now stale relative to the copy this just
			// pulled, so if the user edits again, the very next push will report ANOTHER
			// conflict against a copy that is actually already applied. That routes back
			// through this same method rather than silently overwriting anything — an extra
			// round trip, not a correctness or data-loss problem.
			RemoteDraftMaterializer.ApplyRawBytes(draft, providerDraftId, draft.ProviderRevision, clock.GetUtcNow(), raw.RawBytes);
			await context.SaveChangesAsync(ct);
		}
		else
		{
			// No remote copy ever existed to prefer — nothing to pull, just clear the flag.
			draft.SyncConflict = false;
			await context.SaveChangesAsync(ct);
		}

		await events.DraftUpdatedAsync(draft.Id);
		return draft;
	}

	public async Task DeleteAsync(Guid draftId, CancellationToken ct = default)
	{
		var draft = await context.Drafts.FirstOrDefaultAsync(d => d.Id == draftId, ct);
		if (draft is null)
		{
			return;
		}

		context.Drafts.Remove(draft);
		await context.SaveChangesAsync(ct);

		// After the local delete: the user asked for it, and a server that refuses must not
		// leave the draft sitting in front of them.
		if (draft.ProviderDraftId is string remoteId)
		{
			await remote.RemoveRemoteAsync(draft.AccountId, remoteId, ct);
		}

		await events.DraftUpdatedAsync(draftId);
	}

	public async Task<DraftAttachment> AddAttachmentAsync(
		Guid draftId,
		string filename,
		string mimeType,
		byte[] content,
		CancellationToken ct = default
	)
	{
		var draft = await context.Drafts.FirstAsync(d => d.Id == draftId, ct);
		var attachment = new DraftAttachment
		{
			Id = Guid.NewGuid(),
			Filename = filename,
			MimeType = mimeType,
			Size = content.LongLength,
			Content = content,
		};
		draft.Attachments = [.. draft.Attachments, attachment];
		draft.SavedAt = clock.GetUtcNow();
		await context.SaveChangesAsync(ct);
		dispatcher.RequestPush(draft.AccountId);
		await events.DraftUpdatedAsync(draft.Id);
		return attachment;
	}

	public async Task RemoveAttachmentAsync(Guid draftId, Guid attachmentId, CancellationToken ct = default)
	{
		var draft = await context.Drafts.FirstAsync(d => d.Id == draftId, ct);
		var remaining = draft.Attachments.Where(a => a.Id != attachmentId).ToArray();
		if (remaining.Length == draft.Attachments.Count)
		{
			return;
		}
		draft.Attachments = remaining;
		draft.SavedAt = clock.GetUtcNow();
		await context.SaveChangesAsync(ct);
		dispatcher.RequestPush(draft.AccountId);
		await events.DraftUpdatedAsync(draft.Id);
	}

	/// <summary>
	/// Queues a draft for sending, after the account's undo-send delay, or at an explicit
	/// future time if <paramref name="scheduledFor"/> is given.
	/// </summary>
	/// <remarks>
	/// The draft is not deleted here. It remains the authoring document until the send
	/// actually succeeds, because a send can be cancelled inside its window or fail outright,
	/// and a user whose cancelled message had already been destroyed would have lost their
	/// work (§15).
	/// </remarks>
	/// <exception cref="InvalidOperationException">
	/// The draft has an unresolved <see cref="Draft.SyncConflict"/>. Send builds its MIME
	/// straight from this editor's local fields with no revision check of its own (§1, §15) —
	/// it never discovers a conflicting remote copy on its own account. Refusing here is what
	/// makes "prompts resolution rather than overwriting" actually true for send, not just for
	/// the ordinary background push <see cref="ResolveConflictAsync"/> already guards.
	/// </exception>
	public async Task<OutboxItem> SendAsync(
		Guid draftId,
		DateTimeOffset? scheduledFor = null,
		CancellationToken ct = default
	)
	{
		var draft = await context.Drafts.FirstAsync(d => d.Id == draftId, ct);
		if (draft.SyncConflict)
		{
			throw new InvalidOperationException(
				"This draft has an unresolved sync conflict. Resolve it before sending."
			);
		}
		var account = await context.Accounts.FirstAsync(a => a.Id == draft.AccountId, ct);

		return await outbox.QueueAsync(account, draft.Id, scheduledFor, ct: ct);
	}

	/// <summary>Cancels a queued send, if it has not already been taken for dispatch.</summary>
	public Task<bool> CancelSendAsync(Guid outboxItemId, CancellationToken ct = default) =>
		outbox.TryCancelAsync(outboxItemId, ct);

	/// <summary>
	/// The account's default send identity.
	/// </summary>
	/// <remarks>
	/// Its address is the authoritative address for the account, so a draft with no identity
	/// would have no defensible From. Choosing at compose time is a later feature; the default
	/// is the one every account is guaranteed to have.
	/// </remarks>
	private async Task<Guid> DefaultIdentityAsync(Guid accountId, CancellationToken ct) =>
		await context
			.SendIdentities.Where(i => i.AccountId == accountId && i.IsDefault)
			.Select(i => i.Id)
			.FirstAsync(ct);
}
