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
				var present = await ExistingUidsAsync(folder, group, ct);
				var existing = group
					.Where(item => present.Contains(item.Uid))
					.Select(item => item.Uid)
					.ToList();

				// Absolute per field, never a toggle — which is what makes a replay safe (§6).
				// One UID-set command per changed field keeps multi-select a native batch.
				if (existing.Count > 0 && update.IsRead is bool read)
				{
					if (read)
					{
						await folder.AddFlagsAsync(existing, MessageFlags.Seen, true, ct);
					}
					else
					{
						await folder.RemoveFlagsAsync(existing, MessageFlags.Seen, true, ct);
					}
				}
				if (existing.Count > 0 && update.IsFlagged is bool flagged)
				{
					if (flagged)
					{
						await folder.AddFlagsAsync(existing, MessageFlags.Flagged, true, ct);
					}
					else
					{
						await folder.RemoveFlagsAsync(existing, MessageFlags.Flagged, true, ct);
					}
				}

				return
				[
					.. group.Select(item =>
						present.Contains(item.Uid)
							? new BatchItemResult(
								item.Reference.MessageId,
								item.Reference.MailboxId,
								true,
								null,
								[]
							)
							: NotFound(item.Reference)
					),
				];
			},
			ct
		);


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
				var present = await ExistingUidsAsync(folder, group, ct);
				var existing = group.Where(item => present.Contains(item.Uid)).ToList();
				UniqueIdMap? moved = null;
				if (existing.Count > 0)
				{
					moved = await folder.MoveToAsync(
						[.. existing.Select(item => item.Uid)],
						destination,
						ct
					);
				}

				var results = new List<BatchItemResult>(group.Count);
				foreach (var (reference, uid) in group)
				{
					if (!present.Contains(uid))
					{
						results.Add(NotFound(reference));
						continue;
					}

					var destinationUid = UniqueId.Invalid;
					var hasDestinationUid = moved is not null
						&& moved.TryGetValue(uid, out destinationUid);
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
									hasDestinationUid ? destinationUid.Id.ToString() : null,
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
				var present = await ExistingUidsAsync(folder, group, ct);
				var existing = group
					.Where(item => present.Contains(item.Uid))
					.Select(item => item.Uid)
					.ToList();
				if (existing.Count > 0)
				{
					await folder.AddFlagsAsync(existing, MessageFlags.Deleted, true, ct);
					await folder.ExpungeAsync(existing, ct);
				}

				return
				[
					.. group.Select(item =>
						present.Contains(item.Uid)
							? new BatchItemResult(
								item.Reference.MessageId,
								item.Reference.MailboxId,
								true,
								null,
								[
									new OccurrenceChange(
										item.Reference.MailboxId,
										null,
										Removed: true
									),
								]
							)
							: NotFound(item.Reference)
					),
				];
			},
			ct
		);

	private static async Task<HashSet<UniqueId>> ExistingUidsAsync(
		IMailFolder folder,
		IReadOnlyList<(MessageOccurrenceRef Reference, UniqueId Uid)> group,
		CancellationToken ct
	) =>
		(await folder.SearchAsync(
			MailKit.Search.SearchQuery.Uids([.. group.Select(item => item.Uid)]),
			ct
		)).ToHashSet();

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
