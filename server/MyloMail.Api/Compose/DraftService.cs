using Microsoft.EntityFrameworkCore;
using MyloMail.Api.Domain;
using MyloMail.Api.Hubs;
using MyloMail.Api.Outbox;
using MyloMail.Api.Persistence;

namespace MyloMail.Api.Compose;

/// <summary>What the compose window sends when it saves.</summary>
public sealed record DraftInput(
	Guid? DraftId,
	Guid AccountId,
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

		draft.SendIdentityId = await DefaultIdentityAsync(input.AccountId, ct);
		draft.InReplyToMessageId = input.InReplyToMessageId;
		draft.To = input.To;
		draft.Cc = input.Cc;
		draft.Bcc = input.Bcc;
		draft.Subject = input.Subject;
		draft.BodyHtml = input.BodyHtml;
		draft.SavedAt = clock.GetUtcNow();

		await context.SaveChangesAsync(ct);
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
		await events.DraftUpdatedAsync(draftId);
	}

	/// <summary>
	/// Queues a draft for sending, after the account's undo-send delay.
	/// </summary>
	/// <remarks>
	/// The draft is not deleted here. It remains the authoring document until the send
	/// actually succeeds, because a send can be cancelled inside its window or fail outright,
	/// and a user whose cancelled message had already been destroyed would have lost their
	/// work (§15).
	/// </remarks>
	public async Task<OutboxItem> SendAsync(Guid draftId, CancellationToken ct = default)
	{
		var draft = await context.Drafts.FirstAsync(d => d.Id == draftId, ct);
		var account = await context.Accounts.FirstAsync(a => a.Id == draft.AccountId, ct);

		return await outbox.QueueAsync(account, draft.Id, ct: ct);
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
