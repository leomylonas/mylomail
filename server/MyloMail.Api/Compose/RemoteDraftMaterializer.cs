using System.Net;
using Microsoft.EntityFrameworkCore;
using MimeKit;
using MimeKit.Text;
using MyloMail.Api.Domain;
using MyloMail.Api.Persistence;
using MyloMail.Api.Providers;
using MyloMail.Api.Providers.Contracts;
using MyloMail.Api.Sync;

namespace MyloMail.Api.Compose;

/// <summary>
/// Turns an observation in a Drafts mailbox into the structured local authoring document
/// described by §1. Draft observations never pass through <see cref="Sync.MessageIngestor"/>.
/// </summary>
/// <remarks>
/// The raw MIME is captured before the page transaction, then the bytes themselves travel in a
/// staged Gmail page. Re-fetching at replay time would let a cursor cover a draft that was
/// deleted before coverage completed, which is precisely the silent skip §3 forbids.
/// </remarks>
public sealed class RemoteDraftMaterializer(MyloMailDbContext context, IMailProviderFactory providers)
{
	public async Task<IReadOnlyList<RemoteDraftPayload>> PrepareAsync(
		Account account,
		IReadOnlyList<MessageDto> messages,
		IReadOnlyDictionary<string, Mailbox> mailboxesByProviderId,
		CancellationToken ct = default
	)
	{
		var provider = providers.For(account);
		var drafts = new List<RemoteDraftPayload>();

		foreach (var message in messages)
		{
			var occurrence = message.Occurrences.FirstOrDefault(o =>
				mailboxesByProviderId.TryGetValue(o.ProviderMailboxId, out var mailbox)
				&& mailbox.EffectiveSpecialUse == SpecialUse.Drafts
			);
			if (occurrence is null || !mailboxesByProviderId.TryGetValue(occurrence.ProviderMailboxId, out var mailbox))
			{
				continue;
			}

			var providerDraftId = message.ProviderStableId ?? occurrence.ProviderOccurrenceId;
			var providerRevision = message.ProviderRevision;
			if (providerRevision is null)
			{
				// A remotely materialised draft is considered clean. Without the revision it was
				// read at, the next local edit could overwrite a concurrent server edit, so this
				// page is incomplete. It must fail before its coverage token or change cursor is
				// committed; silently dropping it would make that loss permanent (§3).
				throw new InvalidOperationException(
					$"Provider returned draft {providerDraftId} without its required revision."
				);
			}
			if (drafts.Any(d => d.ProviderDraftId == providerDraftId))
			{
				continue;
			}

			var raw = await provider.FetchRawMessageAsync(
				account,
				new MessageOccurrenceRef(Guid.Empty, mailbox.Id, occurrence.ProviderOccurrenceId),
				ct
			);
			drafts.Add(
				new RemoteDraftPayload(
					occurrence.ProviderMailboxId,
					providerDraftId,
					providerRevision,
					message.ReceivedAt,
					raw.RawBytes
				)
			);
		}

		return drafts;
	}

	/// <summary>Upserts captured drafts inside the same transaction as their sync page.</summary>
	public async Task<IReadOnlyList<Guid>> ApplyAsync(
		Account account,
		IReadOnlyList<RemoteDraftPayload> incoming,
		IReadOnlyDictionary<string, Mailbox> mailboxesByProviderId,
		GenerationSnapshot generations,
		CancellationToken ct = default
	)
	{
		var changed = new List<Guid>();
		foreach (var payload in incoming)
		{
			if (!mailboxesByProviderId.TryGetValue(payload.ProviderMailboxId, out var mailbox)
				|| !generations.StillCurrent(payload.ProviderMailboxId, mailbox))
			{
				continue;
			}

			var draft = await context.Drafts.FirstOrDefaultAsync(
				d => d.AccountId == account.Id && d.ProviderDraftId == payload.ProviderDraftId,
				ct
			);
			if (draft is not null && draft.PushedAt is not null && draft.PushedAt < draft.SavedAt)
			{
				// Local intent is newer. Do not replace it with a server copy that was observed
				// concurrently; merging authoring documents would manufacture a third version.
				draft.SyncConflict = true;
				changed.Add(draft.Id);
				continue;
			}

			using var stream = new MemoryStream(payload.RawBytes);
			var mime = await MimeMessage.LoadAsync(stream, ct);
			if (draft is null)
			{
				draft = new Draft
				{
					Id = Guid.NewGuid(),
					AccountId = account.Id,
					SendIdentityId = await IdentityAsync(account.Id, mime, ct),
				};
				context.Drafts.Add(draft);
			}

			Apply(mime, payload, draft);
			changed.Add(draft.Id);
		}

		return changed;
	}

	public async Task<IReadOnlyList<Guid>> ApplyRemovalsAsync(
		Account account,
		IReadOnlyList<OccurrenceRemoval> removals,
		IReadOnlyDictionary<string, Mailbox> mailboxesByProviderId,
		GenerationSnapshot generations,
		CancellationToken ct = default
	)
	{
		var changed = new List<Guid>();
		foreach (var removal in removals)
		{
			if (!mailboxesByProviderId.TryGetValue(removal.ProviderMailboxId, out var mailbox)
				|| mailbox.EffectiveSpecialUse != SpecialUse.Drafts
				|| !generations.StillCurrent(removal.ProviderMailboxId, mailbox))
			{
				continue;
			}

			var draft = await context.Drafts.FirstOrDefaultAsync(
				d => d.AccountId == account.Id && d.ProviderDraftId == removal.ProviderOccurrenceId,
				ct
			);
			if (draft is null)
			{
				continue;
			}

			if (draft.PushedAt is null || draft.PushedAt < draft.SavedAt)
			{
				draft.SyncConflict = true;
				changed.Add(draft.Id);
				continue;
			}

			context.Drafts.Remove(draft);
			changed.Add(draft.Id);
		}
		return changed;
	}

	private async Task<Guid> IdentityAsync(Guid accountId, MimeMessage mime, CancellationToken ct)
	{
		var from = mime.From.Mailboxes.FirstOrDefault()?.Address;
		if (from is not null)
		{
			var matching = await context.SendIdentities.FirstOrDefaultAsync(
				i => i.AccountId == accountId && i.EmailAddress.ToLower() == from.ToLower(),
				ct
			);
			if (matching is not null)
			{
				return matching.Id;
			}
		}

		return await context.SendIdentities.Where(i => i.AccountId == accountId && i.IsDefault).Select(i => i.Id).FirstAsync(ct);
	}

	/// <summary>
	/// The same MIME-to-<see cref="Draft"/> field-copying an ordinary sync observation uses,
	/// exposed for a conflict resolution's "keep theirs" (§1, §15) to reuse rather than
	/// re-implement — one parser for what a remote draft's bytes mean, not two.
	/// </summary>
	internal static void ApplyRawBytes(
		Draft draft,
		string providerDraftId,
		string? providerRevision,
		DateTimeOffset savedAt,
		byte[] rawBytes
	)
	{
		using var stream = new MemoryStream(rawBytes);
		var mime = MimeMessage.Load(stream);
		Apply(mime, new RemoteDraftPayload(string.Empty, providerDraftId, providerRevision, savedAt, rawBytes), draft);
	}

	private static void Apply(MimeMessage mime, RemoteDraftPayload payload, Draft draft)
	{
		draft.To = Addresses(mime.To);
		draft.Cc = Addresses(mime.Cc);
		draft.Bcc = Addresses(mime.Bcc);
		draft.Subject = mime.Subject ?? string.Empty;
		draft.BodyHtml = mime.GetTextBody(TextFormat.Html) ?? PlainHtml(mime.GetTextBody(TextFormat.Plain));
		draft.Attachments = [.. Attachments(mime)];
		draft.ProviderDraftId = payload.ProviderDraftId;
		draft.ProviderRevision = payload.ProviderRevision;
		draft.SavedAt = payload.SavedAt;
		draft.PushedAt = payload.SavedAt;
		draft.SyncConflict = false;
	}

	private static IReadOnlyList<Address> Addresses(InternetAddressList addresses) =>
		[.. addresses.Mailboxes.Select(mailbox => new Address(mailbox.Name, mailbox.Address))];

	// `MimeMessage.Attachments` only enumerates parts whose Content-Disposition is literally
	// "attachment" (MimeKit's own documented behaviour) — an inline image (Content-Disposition:
	// inline, or no disposition at all, referenced from the body by cid:) is invisible to it
	// entirely, not merely misclassified. Walking every leaf MimePart directly, the same way
	// ContentAcquisition.cs already does for received messages, is what actually captures an
	// inline image's bytes instead of silently dropping them from the materialised draft.
	internal static IEnumerable<DraftAttachment> Attachments(MimeMessage mime)
	{
		var iterator = new MimeIterator(mime);
		while (iterator.MoveNext())
		{
			if (iterator.Current is not MimePart part || part.Content is null)
			{
				continue;
			}

			var isInline = part.ContentDisposition?.IsAttachment != true;
			if (isInline && part.ContentId is null && part.FileName is null)
			{
				// A body part rather than something the user would recognise as attached.
				continue;
			}

			using var content = new MemoryStream();
			part.Content.DecodeTo(content);
			yield return new DraftAttachment
			{
				Id = Guid.NewGuid(),
				Filename = part.FileName ?? "attachment",
				MimeType = part.ContentType.MimeType,
				Size = content.Length,
				ContentId = part.ContentId,
				IsInline = isInline,
				Content = content.ToArray(),
			};
		}
	}

	private static string PlainHtml(string? plain) =>
		string.IsNullOrEmpty(plain) ? string.Empty : $"<pre>{WebUtility.HtmlEncode(plain)}</pre>";
}

/// <summary>Captured raw draft data that may be persisted within a staged change page.</summary>
public sealed record RemoteDraftPayload(
	string ProviderMailboxId,
	string ProviderDraftId,
	string? ProviderRevision,
	DateTimeOffset SavedAt,
	byte[] RawBytes
);
