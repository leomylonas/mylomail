using Google;
using Google.Apis.Gmail.v1;
using Google.Apis.Gmail.v1.Data;
using MyloMail.Api.Domain;
using MyloMail.Api.Providers.Contracts;

namespace MyloMail.Api.Providers.Gmail;

public sealed partial class GmailMailProvider
{
	public async Task<BatchResult> SetFlagsAsync(
		Account account,
		IReadOnlyList<MessageOccurrenceRef> refs,
		FlagUpdate update,
		CancellationToken ct
	)
	{
		var service = await ServiceAsync(account, ct);
		var (existing, items) = await ExistingAsync(service, refs, ct);
		await BatchModifyAsync(service, existing, LabelsToAdd(update), LabelsToRemove(update), ct);
		items.AddRange(
			existing.Select(reference =>
				new BatchItemResult(reference.MessageId, reference.MailboxId, true, null, [])
			)
		);
		return new BatchResult(items);
	}

	public async Task<BatchResult> MoveMessagesAsync(
		Account account,
		IReadOnlyList<MessageOccurrenceRef> refs,
		Mailbox target,
		CancellationToken ct
	) => await ModifyLabelsAsync(
		account,
		refs,
		add: [ProviderMailboxId(target)],
		remove: null,
		change: reference =>
		[
			new OccurrenceChange(reference.MailboxId, null, Removed: true),
			new OccurrenceChange(target.Id, reference.ProviderOccurrenceId, Removed: false),
		],
		ct,
		removeSourceLabel: true
	);

	public async Task<BatchResult> RemoveFromMailboxAsync(
		Account account,
		IReadOnlyList<MessageOccurrenceRef> refs,
		CancellationToken ct
	) => await ModifyLabelsAsync(
		account,
		refs,
		add: null,
		remove: null,
		change: reference => [new OccurrenceChange(reference.MailboxId, null, Removed: true)],
		ct,
		removeSourceLabel: true
	);

	/// <summary>
	/// Adds Gmail's own <c>TRASH</c> label via the dedicated trash endpoint rather than a
	/// label-modify call — Gmail's API reserves this transition for it, and it is what makes
	/// the message eligible for the 30-day auto-purge Gmail's own UI relies on.
	/// </summary>
	public async Task<BatchResult> MoveToTrashAsync(
		Account account,
		IReadOnlyList<MessageOccurrenceRef> refs,
		CancellationToken ct
	)
	{
		var service = await ServiceAsync(account, ct);
		var items = new List<BatchItemResult>(refs.Count);
		foreach (var reference in refs)
		{
			try
			{
				await service.Users.Messages.Trash(UserId, reference.ProviderOccurrenceId).ExecuteAsync(ct);
				items.Add(
					new BatchItemResult(
						reference.MessageId,
						reference.MailboxId,
						true,
						null,
						[new OccurrenceChange(reference.MailboxId, null, Removed: true)]
					)
				);
			}
			catch (GoogleApiException ex) when (ex.HttpStatusCode == System.Net.HttpStatusCode.NotFound)
			{
				items.Add(NotFound(reference));
			}
		}

		return new BatchResult(items);
	}

	public async Task<BatchResult> DeletePermanentlyAsync(
		Account account,
		IReadOnlyList<MessageOccurrenceRef> refs,
		CancellationToken ct
	)
	{
		var service = await ServiceAsync(account, ct);
		var items = new List<BatchItemResult>(refs.Count);
		foreach (var group in refs.Chunk(1000))
		{
			var (existing, missing) = await ExistingAsync(service, group, ct);
			items.AddRange(missing);
			await service.Users.Messages
				.BatchDelete(new BatchDeleteMessagesRequest { Ids = [.. existing.Select(r => r.ProviderOccurrenceId)] }, UserId)
				.ExecuteAsync(ct);
			items.AddRange(
				existing.Select(reference =>
					new BatchItemResult(
						reference.MessageId,
						reference.MailboxId,
						true,
						null,
						[new OccurrenceChange(reference.MailboxId, null, Removed: true)]
					)
				)
			);
		}

		return new BatchResult(items);
	}

	private async Task<BatchResult> ModifyLabelsAsync(
		Account account,
		IReadOnlyList<MessageOccurrenceRef> refs,
		IList<string>? add,
		IList<string>? remove,
		Func<MessageOccurrenceRef, IReadOnlyList<OccurrenceChange>> change,
		CancellationToken ct,
		bool removeSourceLabel = false
	)
	{
		var items = new List<BatchItemResult>(refs.Count);
		var service = await ServiceAsync(account, ct);
		foreach (var group in refs.GroupBy(reference => reference.MailboxId))
		{
			var groupRefs = group.ToList();
			var removeLabels = removeSourceLabel
				? new List<string> { mailboxes.ProviderMailboxId(group.Key) }
				: remove;
			var (existing, missing) = await ExistingAsync(service, groupRefs, ct);
			items.AddRange(missing);
			await BatchModifyAsync(service, existing, add, removeLabels, ct);
			items.AddRange(
				existing.Select(reference =>
					new BatchItemResult(reference.MessageId, reference.MailboxId, true, null, change(reference))
				)
			);
		}

		return new BatchResult(items);
	}

	private static async Task<(IReadOnlyList<MessageOccurrenceRef> Existing, List<BatchItemResult> Missing)> ExistingAsync(
		GmailService service,
		IEnumerable<MessageOccurrenceRef> refs,
		CancellationToken ct
	)
	{
		var existing = new List<MessageOccurrenceRef>();
		var missing = new List<BatchItemResult>();
		foreach (var reference in refs)
		{
			try
			{
				await service.Users.Messages.Get(UserId, reference.ProviderOccurrenceId).ExecuteAsync(ct);
				existing.Add(reference);
			}
			catch (GoogleApiException ex) when (ex.HttpStatusCode == System.Net.HttpStatusCode.NotFound)
			{
				missing.Add(NotFound(reference));
			}
		}

		return (existing, missing);
	}

	private static Task BatchModifyAsync(
		GmailService service,
		IReadOnlyList<MessageOccurrenceRef> refs,
		IList<string>? add,
		IList<string>? remove,
		CancellationToken ct
	) => refs.Count == 0
		? Task.CompletedTask
		: service.Users.Messages
			.BatchModify(
				new BatchModifyMessagesRequest
				{
					Ids = [.. refs.Select(reference => reference.ProviderOccurrenceId)],
					AddLabelIds = add,
					RemoveLabelIds = remove,
				},
				UserId
			)
			.ExecuteAsync(ct);

	private static IList<string> LabelsToAdd(FlagUpdate update)
	{
		var labels = new List<string>();
		if (update.IsRead is false)
		{
			labels.Add("UNREAD");
		}
		if (update.IsFlagged is true)
		{
			labels.Add("STARRED");
		}
		return labels;
	}

	private static IList<string> LabelsToRemove(FlagUpdate update)
	{
		var labels = new List<string>();
		if (update.IsRead is true)
		{
			labels.Add("UNREAD");
		}
		if (update.IsFlagged is false)
		{
			labels.Add("STARRED");
		}
		return labels;
	}

	private static BatchItemResult NotFound(MessageOccurrenceRef reference) =>
		new(
			reference.MessageId,
			reference.MailboxId,
			false,
			Problem(
				"Message not found",
				"The message is no longer present in that mailbox on Google.",
				"NOTFOUND"
			),
			[]
		);
}
