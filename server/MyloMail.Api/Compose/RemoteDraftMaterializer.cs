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
	internal const int MaximumRawDraftBytes = 160 * 1024 * 1024;
	private const int MaximumRemoteDraftPageBytes = 160 * 1024 * 1024;
	private const int MaximumDraftAttachmentCount = 512;
	private const int MaximumMimeEntities = 4096;
	private const int MaximumMimeDepth = 32;
	public async Task<IReadOnlyList<RemoteDraftPayload>> PrepareAsync(
		Account account,
		IReadOnlyList<MessageDto> messages,
		IReadOnlyDictionary<string, Mailbox> mailboxesByProviderId,
		CancellationToken ct = default
	)
	{
		var provider = providers.For(account);
		var drafts = new List<RemoteDraftPayload>();
		var totalRawBytes = 0L;

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

			var providerMessageId = occurrence.ProviderOccurrenceId;
			var providerDraftId = message.ProviderStableId ?? providerMessageId;
			var providerRevision = message.ProviderRevision;
			if (providerRevision is null)
			{
				throw new InvalidOperationException($"Provider returned draft {providerDraftId} without its required revision.");
			}

			if (account.ProviderType == ProviderType.Gmail)
			{
				// Gmail mailbox observations and MIME fetches use message IDs; draft writes
				// use an enclosing draft container. Resolve that container before staging,
				// without treating nullable/non-unique RFC Message-ID metadata as identity.
				var container = await provider.FindDraftByMessageIdAsync(account, providerMessageId, ct)
					?? throw new InvalidOperationException("The observed Gmail draft disappeared before its container identity was resolved.");
				providerDraftId = container.ProviderDraftId;
			}
			if (drafts.Any(d => d.ProviderDraftId == providerDraftId))
			{
				continue;
			}

			var raw = await provider.FetchRawMessageAsync(
				account,
				new MessageOccurrenceRef(Guid.Empty, mailbox.Id, providerMessageId),
				ct,
				MaximumRawDraftBytes
			);
			if (totalRawBytes + raw.RawBytes.Length > MaximumRemoteDraftPageBytes)
			{
				throw new InvalidOperationException($"Provider draft page exceeds the {MaximumRemoteDraftPageBytes}-byte raw MIME limit.");
			}
			ValidateRawBytes(raw.RawBytes);
			totalRawBytes += raw.RawBytes.Length;
			using (var stream = new MemoryStream(raw.RawBytes))
			{
				var mime = await MimeMessage.LoadAsync(stream, ct);
				EnsureMimeStructure(mime);
				EnsureAttachmentCount(mime);
			}
			drafts.Add(new RemoteDraftPayload(
				occurrence.ProviderMailboxId,
				providerDraftId,
				providerMessageId,
				providerRevision,
				message.ReceivedAt,
				raw.RawBytes
			));
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

			var providerMessageId = payload.ProviderMessageId ?? payload.ProviderDraftId;
			var isLegacyGmailPayload =
				account.ProviderType == ProviderType.Gmail && string.IsNullOrEmpty(payload.ProviderMessageId);
			var draft = await context.Drafts.FirstOrDefaultAsync(
				d => d.AccountId == account.Id
					&& (
						d.ProviderDraftId == payload.ProviderDraftId
						|| d.ProviderMessageId == providerMessageId
						|| (d.ProviderMessageId == null && d.ProviderDraftId == providerMessageId)
					),
				ct
			);
			if (draft is not null && (draft.SyncConflict || (draft.PushedAt is not null && draft.PushedAt < draft.SavedAt)))
			{
				// Local intent is newer. Do not replace it with a server copy that was observed
				// concurrently; merging authoring documents would manufacture a third version.
				if (!isLegacyGmailPayload)
				{
					draft.ProviderDraftId = payload.ProviderDraftId;
					draft.ProviderMessageId = providerMessageId;
					draft.ProviderRevision = payload.ProviderRevision;
				}
				draft.SyncConflict = true;
				changed.Add(draft.Id);
				continue;
			}

			ValidateRawBytes(payload.RawBytes);
			using var stream = new MemoryStream(payload.RawBytes);
			var mime = await MimeMessage.LoadAsync(stream, ct);
			EnsureMimeStructure(mime);
			EnsureAttachmentCount(mime);
			if (draft is null && mime.Headers[HeaderId.MessageId] is { Length: > 0 } stableMessageId)
			{
				// An initial create may have reached the server before its response committed.
				// Bind that observed remote identity to the original local authoring document
				// instead of creating a competing local draft owner (§1, §6).
				draft = await context.Drafts.FirstOrDefaultAsync(
					d => d.AccountId == account.Id && d.ProviderDraftId == null && d.StableMessageId == stableMessageId,
					ct
				);
				if (draft is not null)
				{
					draft.ProviderDraftId = isLegacyGmailPayload ? null : payload.ProviderDraftId;
					draft.ProviderMessageId = providerMessageId;
					draft.ProviderRevision = payload.ProviderRevision;
					draft.PushedAt = payload.SavedAt;
					draft.PushDispatchedForSavedAt = null;
					draft.SyncConflict = true;
					changed.Add(draft.Id);
					continue;
				}
			}
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
			if (isLegacyGmailPayload)
			{
				// Older staged Gmail payloads recorded the underlying message id in the only
				// identity slot. The enclosing Draft id cannot be reconstructed inside this
				// page transaction, so retain the observed content as an explicit conflict
				// rather than falsely addressing a later write/delete to that message id.
				draft.ProviderDraftId = null;
				draft.ProviderMessageId = providerMessageId;
				draft.SyncConflict = true;
			}
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
				d => d.AccountId == account.Id
					&& (
						d.ProviderMessageId == removal.ProviderOccurrenceId
						|| (d.ProviderMessageId == null && d.ProviderDraftId == removal.ProviderOccurrenceId)
					),
				ct
			);
			if (draft is null)
			{
				continue;
			}

			if (draft.SyncConflict || draft.PushedAt is null || draft.PushedAt < draft.SavedAt)
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
		ValidateRawBytes(rawBytes);
		using var stream = new MemoryStream(rawBytes);
		var mime = MimeMessage.Load(stream);
		EnsureMimeStructure(mime);
		EnsureAttachmentCount(mime);
		Apply(
			mime,
			new RemoteDraftPayload(
				string.Empty,
				providerDraftId,
				draft.ProviderMessageId ?? providerDraftId,
				providerRevision,
				savedAt,
				rawBytes
			),
			draft
		);
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
		draft.ProviderMessageId = payload.ProviderMessageId;
		draft.ProviderRevision = payload.ProviderRevision;
		draft.SavedAt = payload.SavedAt;
		draft.PushedAt = payload.SavedAt;
		draft.SyncConflict = false;
	}

	private static void ValidateRawBytes(byte[] rawBytes)
	{
		if (rawBytes.Length > MaximumRawDraftBytes)
		{
			throw new InvalidOperationException($"Provider draft exceeds the {MaximumRawDraftBytes}-byte raw MIME limit.");
		}
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
		var attachmentCount = 0;
		while (iterator.MoveNext())
		{
			if (iterator.Current is not MimePart part || part.Content is null || !IsUserAttachment(part, out var isInline))
			{
				continue;
			}

			EnsureAttachmentCount(ref attachmentCount);
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

	private static void EnsureMimeStructure(MimeMessage mime)
	{
		var entities = 0;
		Visit(mime.Body, 1);
		return;

		void Visit(MimeEntity? entity, int depth)
		{
			if (entity is null)
			{
				return;
			}
			if (depth > MaximumMimeDepth || ++entities > MaximumMimeEntities)
			{
				throw new InvalidOperationException("Provider draft exceeds the MIME structure limit.");
			}
			if (entity is Multipart multipart)
			{
				foreach (var child in multipart)
				{
					Visit(child, depth + 1);
				}
			}
			else if (entity is MessagePart messagePart)
			{
				Visit(messagePart.Message?.Body, depth + 1);
			}
		}
	}

	internal static void EnsureAttachmentCount(MimeMessage mime)
	{
		var iterator = new MimeIterator(mime);
		var attachmentCount = 0;
		while (iterator.MoveNext())
		{
			if (iterator.Current is MimePart part && part.Content is not null && IsUserAttachment(part, out _))
			{
				EnsureAttachmentCount(ref attachmentCount);
			}
		}
	}

	private static bool IsUserAttachment(MimePart part, out bool isInline)
	{
		isInline = part.ContentDisposition?.IsAttachment != true;
		return !isInline || part.ContentId is not null || part.FileName is not null;
	}

	private static void EnsureAttachmentCount(ref int attachmentCount)
	{
		if (++attachmentCount > MaximumDraftAttachmentCount)
		{
			throw new InvalidOperationException($"Provider draft exceeds the {MaximumDraftAttachmentCount}-attachment limit.");
		}
	}

	private static string PlainHtml(string? plain) =>
		string.IsNullOrEmpty(plain) ? string.Empty : $"<pre>{WebUtility.HtmlEncode(plain)}</pre>";
}

/// <summary>Captured raw draft data that may be persisted within a staged change page.</summary>
public sealed record RemoteDraftPayload(
	string ProviderMailboxId,
	string ProviderDraftId,
	string ProviderMessageId,
	string? ProviderRevision,
	DateTimeOffset SavedAt,
	byte[] RawBytes
);
