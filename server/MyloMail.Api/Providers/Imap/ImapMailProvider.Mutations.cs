using MailKit;
using MailKit.Net.Imap;
using MyloMail.Api.Domain;
using MyloMail.Api.Errors;
using MyloMail.Api.Providers.Contracts;

namespace MyloMail.Api.Providers.Imap;

public sealed partial class ImapMailProvider
{
	public Task<BatchResult> SetFlagsAsync(
		Account account,
		IReadOnlyList<MessageOccurrenceRef> refs,
		FlagUpdate update,
		CancellationToken ct
	) =>
		PerMailboxAsync(
			refs,
			async (client, folder, group, ct) =>
			{
				var results = new List<BatchItemResult>(group.Count);

				// Absolute per field, never a toggle — which is what makes a replay safe (§6).
				// Null means leave alone, so a mixed multi-select cannot clobber a flag the
				// user did not touch.
				foreach (var (reference, uid) in group)
				{
					var present = await folder.SearchAsync(
						MailKit.Search.SearchQuery.Uids(new UniqueIdRange(uid, uid)),
						ct
					);

					if (present.Count == 0)
					{
						results.Add(NotFound(reference));
						continue;
					}

					if (update.IsRead is bool read)
					{
						await ApplyAsync(folder, uid, MessageFlags.Seen, read, ct);
					}

					if (update.IsFlagged is bool flagged)
					{
						await ApplyAsync(folder, uid, MessageFlags.Flagged, flagged, ct);
					}

					results.Add(
						new BatchItemResult(reference.MessageId, reference.MailboxId, true, null, [])
					);
				}

				return results;
			},
			ct
		);

	private static Task ApplyAsync(
		IMailFolder folder,
		UniqueId uid,
		MessageFlags flag,
		bool set,
		CancellationToken ct
	) =>
		set
			? folder.AddFlagsAsync([uid], flag, true, ct)
			: folder.RemoveFlagsAsync([uid], flag, true, ct);

	public Task<BatchResult> MoveMessagesAsync(
		Account account,
		IReadOnlyList<MessageOccurrenceRef> refs,
		Mailbox target,
		CancellationToken ct
	) =>
		PerMailboxAsync(
			refs,
			async (client, folder, group, ct) =>
			{
				var destination = await client.GetFolderAsync(
					target.ProviderMailboxId
						?? throw new InvalidOperationException(
							"IMAP mailboxes always have a provider id"
						),
					ct
				);

				var results = new List<BatchItemResult>(group.Count);

				foreach (var (reference, uid) in group)
				{
					var present = await folder.SearchAsync(
						MailKit.Search.SearchQuery.Uids(new UniqueIdRange(uid, uid)),
						ct
					);
					if (present.Count == 0)
					{
						results.Add(NotFound(reference));
						continue;
					}

					// A move mints a new UID in the destination, so the id the caller holds is
					// dead the moment this succeeds. With UIDPLUS or MOVE the server returns
					// the replacement; otherwise nothing does, and the absent id is what tells
					// the caller to reconcile rather than guess (§2).
					var moved = await folder.MoveToAsync(uid, destination, ct);

					results.Add(
						new BatchItemResult(
							reference.MessageId,
							reference.MailboxId,
							true,
							null,
							[
								new OccurrenceChange(reference.MailboxId, null, Removed: true),
								new OccurrenceChange(
									target.Id,
									moved.HasValue ? moved.Value.Id.ToString() : null,
									Removed: false
								),
							]
						)
					);
				}

				return results;
			},
			ct
		);

	public Task<BatchResult> RemoveFromMailboxAsync(
		Account account,
		IReadOnlyList<MessageOccurrenceRef> refs,
		CancellationToken ct
	) => ExpungeAsync(refs, ct);

	public async Task<BatchResult> MoveToTrashAsync(
		Account account,
		IReadOnlyList<MessageOccurrenceRef> refs,
		CancellationToken ct
	)
	{
		var trashTargets = refs
			.Select(reference => reference.ResolvedTargetMailboxId)
			.Distinct()
			.ToList();
		if (trashTargets.Count != 1 || trashTargets[0] is not Guid trashId)
		{
			return new BatchResult(
				[
					.. refs.Select(reference => new BatchItemResult(
						reference.MessageId,
						reference.MailboxId,
						false,
						Problem(
							ErrorCategory.ProviderRejected,
							"Trash unavailable",
							"The dispatch did not resolve exactly one Trash mailbox.",
							"TRASH_UNAVAILABLE"
						),
						[]
					)),
				]
			);
		}

		var alreadyTrashed = refs
			.Where(reference => reference.MailboxId == trashId)
			.Select(reference => new BatchItemResult(
				reference.MessageId,
				reference.MailboxId,
				true,
				null,
				[]
			))
			.ToList();
		var toMove = refs.Where(reference => reference.MailboxId != trashId).ToList();
		if (toMove.Count == 0)
		{
			return new BatchResult(alreadyTrashed);
		}

		var moved = await MoveMessagesAsync(
			account,
			toMove,
			new Mailbox
			{
				Id = trashId,
				AccountId = account.Id,
				ProviderMailboxId = mailboxes.ProviderMailboxId(trashId),
				SpecialUse = SpecialUse.Trash,
			},
			ct
		);
		return new BatchResult([.. alreadyTrashed, .. moved.Items]);
	}

	public Task<BatchResult> DeletePermanentlyAsync(
		Account account,
		IReadOnlyList<MessageOccurrenceRef> refs,
		CancellationToken ct
	) => ExpungeAsync(refs, ct);

	/// <summary>
	/// Membership removal and permanent deletion flag <c>\Deleted</c> and expunge.
	/// Move-to-trash is deliberately separate and moves into the account's current Trash
	/// mailbox instead.
	/// </summary>
	private Task<BatchResult> ExpungeAsync(
		IReadOnlyList<MessageOccurrenceRef> refs,
		CancellationToken ct
	) =>
		PerMailboxAsync(
			refs,
			async (client, folder, group, ct) =>
			{
				var results = new List<BatchItemResult>(group.Count);

				foreach (var (reference, uid) in group)
				{
					var present = await folder.SearchAsync(
						MailKit.Search.SearchQuery.Uids(new UniqueIdRange(uid, uid)),
						ct
					);

					if (present.Count == 0)
					{
						results.Add(NotFound(reference));
						continue;
					}

					await folder.AddFlagsAsync([uid], MessageFlags.Deleted, true, ct);
					await folder.ExpungeAsync([uid], ct);

					results.Add(
						new BatchItemResult(
							reference.MessageId,
							reference.MailboxId,
							true,
							null,
							[new OccurrenceChange(reference.MailboxId, null, Removed: true)]
						)
					);
				}

				return results;
			},
			ct
		);

	private static BatchItemResult NotFound(MessageOccurrenceRef reference) =>
		new(
			reference.MessageId,
			reference.MailboxId,
			false,
			Problem(
				ErrorCategory.ProviderRejected,
				"Message not found",
				"The message is no longer present in that mailbox on the server.",
				"NOTFOUND"
			),
			[]
		);

	/// <summary>
	/// Groups references by mailbox, because an IMAP UID is only meaningful inside its
	/// folder, and returns one result per submitted reference.
	/// </summary>
	private async Task<BatchResult> PerMailboxAsync(
		IReadOnlyList<MessageOccurrenceRef> refs,
		Func<
			ImapClient,
			IMailFolder,
			IReadOnlyList<(MessageOccurrenceRef Reference, UniqueId Uid)>,
			CancellationToken,
			Task<List<BatchItemResult>>
		> apply,
		CancellationToken ct
	)
	{
		using var client = await ConnectAsync(ct);
		var items = new List<BatchItemResult>(refs.Count);

		foreach (var group in refs.GroupBy(reference => reference.MailboxId))
		{
			var folder = await client.GetFolderAsync(mailboxes.ProviderMailboxId(group.Key), ct);
			await folder.OpenAsync(FolderAccess.ReadWrite, ct);

			var resolved = group
				.Select(reference =>
					(Reference: reference, Uid: new UniqueId(uint.Parse(reference.ProviderOccurrenceId)))
				)
				.ToList();

			items.AddRange(await apply(client, folder, resolved, ct));
			await folder.CloseAsync(false, ct);
		}

		await client.DisconnectAsync(true, ct);
		return new BatchResult(items);
	}
}
