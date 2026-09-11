using Google;
using Google.Apis.Gmail.v1;
using Google.Apis.Gmail.v1.Data;
using Google.Apis.Requests;
using MyloMail.Api.Domain;
using MyloMail.Api.Providers.Contracts;
using GmailMessage = Google.Apis.Gmail.v1.Data.Message;

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
		return await MoveToTrashBatchAsync(service, refs, ct);
	}

	internal static async Task<BatchResult> MoveToTrashBatchAsync(
		GmailService service,
		IReadOnlyList<MessageOccurrenceRef> refs,
		CancellationToken ct
	)
	{
		var items = new List<BatchItemResult>(refs.Count);
		foreach (var group in refs.Chunk(100))
		{
			var batch = new BatchRequest(service);
			var outcomes = new BatchItemResult?[group.Length];
			TimeSpan? retryAfter = null;
			for (var index = 0; index < group.Length; index++)
			{
				var slot = index;
				var reference = group[index];
				batch.Queue<GmailMessage>(
					service.Users.Messages.Trash(UserId, reference.ProviderOccurrenceId),
					(content, error, responseIndex, response) =>
					{
						if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
						{
							var header = response.Headers.RetryAfter;
							var itemDelay =
								header?.Delta
								?? (header?.Date is { } date
									? date - DateTimeOffset.UtcNow
									: (TimeSpan?)null);
							if (itemDelay is not { } positiveDelay || positiveDelay <= TimeSpan.Zero)
							{
								positiveDelay = GmailRequestExtensions.DefaultRetryAfter;
							}
							if (retryAfter is null || positiveDelay > retryAfter)
							{
								retryAfter = positiveDelay;
							}
							return;
						}

						outcomes[slot] =
							response.IsSuccessStatusCode
								? new BatchItemResult(
									reference.MessageId,
									reference.MailboxId,
									true,
									null,
									[
										new OccurrenceChange(
											reference.MailboxId,
											null,
											Removed: true
										),
									]
								)
								: Failed(reference, response.StatusCode);
					}
				);
			}

			await batch.ExecuteThrottleAwareAsync(service, ct);
			if (retryAfter is { } delay)
			{
				throw new ProviderThrottledException(
					delay > TimeSpan.Zero ? delay : GmailRequestExtensions.DefaultRetryAfter,
					"Gmail rate limit exceeded."
				);
			}

			items.AddRange(
				outcomes.Select(outcome =>
					outcome
						?? throw new InvalidOperationException(
							"Gmail omitted an item response from its batch."
						)
				)
			);
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
				.ExecuteThrottleAwareAsync(ct);
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
				await service.Users.Messages.Get(UserId, reference.ProviderOccurrenceId).ExecuteThrottleAwareAsync(ct);
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
			.ExecuteThrottleAwareAsync(ct);

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

	private static BatchItemResult Failed(
		MessageOccurrenceRef reference,
		System.Net.HttpStatusCode status
	) =>
		status == System.Net.HttpStatusCode.NotFound
			? NotFound(reference)
			: new BatchItemResult(
				reference.MessageId,
				reference.MailboxId,
				false,
				Problem(
					"Gmail mutation was rejected",
					$"Gmail returned HTTP {(int)status} for this item.",
					((int)status).ToString()
				),
				[]
			);

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
