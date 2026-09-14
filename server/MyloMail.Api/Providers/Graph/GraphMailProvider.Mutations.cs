using Microsoft.Graph;
using Microsoft.Graph.Me.Messages.Item.Move;
using Microsoft.Graph.Models;
using Microsoft.Kiota.Abstractions;
using MyloMail.Api.Domain;
using MyloMail.Api.Errors;
using MyloMail.Api.Providers.Contracts;
using static MyloMail.Api.Providers.Graph.GraphThrottleAwareRequests;
using DomainMailbox = MyloMail.Api.Domain.Mailbox;
using GraphMessage = Microsoft.Graph.Models.Message;

namespace MyloMail.Api.Providers.Graph;

public sealed partial class GraphMailProvider
{
	public async Task<RawMessageResult> FetchRawMessageAsync(
		Account account,
		MessageOccurrenceRef occurrence,
		CancellationToken ct,
		int? maximumBytes = null
	)
	{
		var client = await ClientAsync(account, ct);
		await using var stream = await ThrottleAwareAsync(
			() => client.Me.Messages[occurrence.ProviderOccurrenceId].Content.GetAsync(null, ct)
		);
		if (stream is null)
		{
			throw new InvalidOperationException("Graph returned no raw MIME stream.");
		}

		return new RawMessageResult(await BoundedContentReader.ReadAsync(stream, maximumBytes, ct));
	}

	public Task<AttachmentConstraints> GetAttachmentConstraintsAsync(Account account, CancellationToken ct) =>
		Task.FromResult(
			new AttachmentConstraints(
				150 * 1024 * 1024,
				null,
				account.AttachmentSizeLimitOverride,
				account.AttachmentSizeLimitOverride is null
			)
		);

	public async Task<BatchResult> SetFlagsAsync(
		Account account,
		IReadOnlyList<MessageOccurrenceRef> refs,
		FlagUpdate update,
		CancellationToken ct
	)
	{
		var client = await ClientAsync(account, ct);
		return await SetFlagsBatchAsync(client, refs, update, ct);
	}

	internal static async Task<BatchResult> SetFlagsBatchAsync(
		GraphServiceClient client,
		IReadOnlyList<MessageOccurrenceRef> refs,
		FlagUpdate update,
		CancellationToken ct
	)
	{
		var items = new List<BatchItemResult>(refs.Count);
		foreach (var group in refs.Chunk(20))
		{
			var batch = new BatchRequestContentCollection(client.RequestAdapter, 20);
			var steps = new Dictionary<string, MessageOccurrenceRef>();
			foreach (var reference in group)
			{
				var message = new GraphMessage();
				if (update.IsRead is bool read)
				{
					message.IsRead = read;
				}
				if (update.IsFlagged is bool flagged)
				{
					message.Flag = new FollowupFlag
					{
						FlagStatus = flagged ? FollowupFlagStatus.Flagged : FollowupFlagStatus.NotFlagged,
					};
				}

				var request = client.Me.Messages[reference.ProviderOccurrenceId].ToPatchRequestInformation(
					message
				);
				SetImmutableIdPreference(request);
				steps[await batch.AddBatchRequestStepAsync(request)] = reference;
			}

			var response = await ThrottleAwareAsync(() => client.Batch.PostAsync(batch, ct));
			TimeSpan? retryAfter = null;
			foreach (var (id, reference) in steps)
			{
				using var itemResponse = await response.GetResponseByIdAsync(id);
				retryAfter = LongestRetryAfter(retryAfter, itemResponse);
				if (itemResponse.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
				{
					continue;
				}
				ThrowIfBatchResponseRequiresRecovery(itemResponse.StatusCode);
				items.Add(
					itemResponse.IsSuccessStatusCode
						? new BatchItemResult(reference.MessageId, reference.MailboxId, true, null, [])
						: Failed(reference, itemResponse.StatusCode)
				);
			}
			ThrowIfBatchThrottled(retryAfter);
		}

		return new BatchResult(items);
	}

	public async Task<BatchResult> MoveMessagesAsync(
		Account account,
		IReadOnlyList<MessageOccurrenceRef> refs,
		DomainMailbox target,
		CancellationToken ct
	)
	{
		var client = await ClientAsync(account, ct);
		return await MoveMessagesBatchAsync(client, refs, target, ct);
	}

	internal static async Task<BatchResult> MoveMessagesBatchAsync(
		GraphServiceClient client,
		IReadOnlyList<MessageOccurrenceRef> refs,
		DomainMailbox target,
		CancellationToken ct
	)
	{
		var destinationId = ProviderMailboxId(target);
		var items = new List<BatchItemResult>(refs.Count);
		foreach (var group in refs.Chunk(20))
		{
			var batch = new BatchRequestContentCollection(client.RequestAdapter, 20);
			var steps = new Dictionary<string, MessageOccurrenceRef>();
			foreach (var reference in group)
			{
				var request = client.Me.Messages[reference.ProviderOccurrenceId].Move.ToPostRequestInformation(
					new MovePostRequestBody { DestinationId = destinationId }
				);
				SetImmutableIdPreference(request);
				steps[await batch.AddBatchRequestStepAsync(request)] = reference;
			}

			var response = await ThrottleAwareAsync(() => client.Batch.PostAsync(batch, ct));
			TimeSpan? retryAfter = null;
			foreach (var (id, reference) in steps)
			{
				using var itemResponse = await response.GetResponseByIdAsync(id);
				retryAfter = LongestRetryAfter(retryAfter, itemResponse);
				if (itemResponse.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
				{
					continue;
				}
				ThrowIfBatchResponseRequiresRecovery(itemResponse.StatusCode);
				if (!itemResponse.IsSuccessStatusCode)
				{
					items.Add(Failed(reference, itemResponse.StatusCode));
					continue;
				}

				var providerId = await MovedProviderIdAsync(itemResponse, ct);
				items.Add(
					new BatchItemResult(
						reference.MessageId,
						reference.MailboxId,
						true,
						null,
						[
							new OccurrenceChange(reference.MailboxId, null, Removed: true),
							new OccurrenceChange(target.Id, providerId, Removed: false),
						]
					)
				);
			}
			ThrowIfBatchThrottled(retryAfter);
		}

		return new BatchResult(items);
	}

	/// <summary>
	/// Moves the occurrence to Graph's well-known <c>deleteditems</c> folder. The execution
	/// reference carries the already-resolved stable local Trash mailbox for the destination
	/// occurrence change.
	/// </summary>
	public async Task<BatchResult> MoveToTrashAsync(
		Account account,
		IReadOnlyList<MessageOccurrenceRef> refs,
		CancellationToken ct
	)
	{
		var client = await ClientAsync(account, ct);
		return await MoveToTrashBatchAsync(client, refs, ct);
	}

	internal static async Task<BatchResult> MoveToTrashBatchAsync(
		GraphServiceClient client,
		IReadOnlyList<MessageOccurrenceRef> refs,
		CancellationToken ct
	)
	{
		const string TrashFolderId = "deleteditems";
		var items = new List<BatchItemResult>(refs.Count);
		foreach (var group in refs.Chunk(20))
		{
			var batch = new BatchRequestContentCollection(client.RequestAdapter, 20);
			var steps = new Dictionary<string, MessageOccurrenceRef>();
			foreach (var reference in group)
			{
				var request = client.Me.Messages[reference.ProviderOccurrenceId].Move.ToPostRequestInformation(
					new MovePostRequestBody { DestinationId = TrashFolderId }
				);
				SetImmutableIdPreference(request);
				steps[await batch.AddBatchRequestStepAsync(request)] = reference;
			}

			var response = await ThrottleAwareAsync(() => client.Batch.PostAsync(batch, ct));
			TimeSpan? retryAfter = null;
			foreach (var (id, reference) in steps)
			{
				using var itemResponse = await response.GetResponseByIdAsync(id);
				retryAfter = LongestRetryAfter(retryAfter, itemResponse);
				if (itemResponse.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
				{
					continue;
				}
				ThrowIfBatchResponseRequiresRecovery(itemResponse.StatusCode);
				if (!itemResponse.IsSuccessStatusCode)
				{
					items.Add(Failed(reference, itemResponse.StatusCode));
					continue;
				}

				var targetId = reference.ResolvedTargetMailboxId
					?? throw new InvalidOperationException(
						"Move-to-Trash requires the resolved local Trash mailbox."
					);
				var providerId = await MovedProviderIdAsync(itemResponse, ct);
				items.Add(
					new BatchItemResult(
						reference.MessageId,
						reference.MailboxId,
						true,
						null,
						[
							new OccurrenceChange(reference.MailboxId, null, Removed: true),
							new OccurrenceChange(targetId, providerId, Removed: false),
						]
					)
				);
			}
			ThrowIfBatchThrottled(retryAfter);
		}

		return new BatchResult(items);
	}

	/// <summary>
	/// Graph messages have exactly one mailbox membership (§2:
	/// <see cref="MyloMail.Api.Providers.ProviderCapabilities.SupportsMultipleMailboxMembership"/>
	/// is false), so there is no "still exists elsewhere" outcome removal from that one mailbox
	/// could mean — the same divergence IMAP resolves by treating this the same as deletion.
	/// </summary>
	public Task<BatchResult> RemoveFromMailboxAsync(
		Account account,
		IReadOnlyList<MessageOccurrenceRef> refs,
		CancellationToken ct
	) => DeletePermanentlyAsync(account, refs, ct);

	public async Task<BatchResult> DeletePermanentlyAsync(
		Account account,
		IReadOnlyList<MessageOccurrenceRef> refs,
		CancellationToken ct
	)
	{
		var client = await ClientAsync(account, ct);
		return await DeletePermanentlyBatchAsync(client, refs, ct);
	}

	internal static async Task<BatchResult> DeletePermanentlyBatchAsync(
		GraphServiceClient client,
		IReadOnlyList<MessageOccurrenceRef> refs,
		CancellationToken ct
	)
	{
		var items = new List<BatchItemResult>(refs.Count);
		foreach (var group in refs.Chunk(20))
		{
			var batch = new BatchRequestContentCollection(client.RequestAdapter, 20);
			var steps = new Dictionary<string, MessageOccurrenceRef>();
			foreach (var reference in group)
			{
				var request = client.Me.Messages[
					reference.ProviderOccurrenceId
				].ToDeleteRequestInformation();
				SetImmutableIdPreference(request);
				steps[await batch.AddBatchRequestStepAsync(request)] = reference;
			}

			var batchResponse = await ThrottleAwareAsync(
				() => client.Batch.PostAsync(batch, ct)
			);
			TimeSpan? retryAfter = null;
			foreach (var (id, reference) in steps)
			{
				using var response = await batchResponse.GetResponseByIdAsync(id);
				retryAfter = LongestRetryAfter(retryAfter, response);
				if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
				{
					continue;
				}
				ThrowIfBatchResponseRequiresRecovery(response.StatusCode);

				items.Add(
					response.IsSuccessStatusCode
						? new BatchItemResult(
							reference.MessageId,
							reference.MailboxId,
							true,
							null,
							[new OccurrenceChange(reference.MailboxId, null, Removed: true)]
						)
						: Failed(reference, response.StatusCode)
				);
			}
			ThrowIfBatchThrottled(retryAfter);
		}

		return new BatchResult(items);
	}

	private static BatchItemResult NotFound(MessageOccurrenceRef reference) =>
		new(
			reference.MessageId,
			reference.MailboxId,
			false,
			new MutationProblemDetails
			{
				Title = "Message not found",
				Detail = "The message is no longer present in that mailbox on Microsoft Graph.",
				Category = ErrorCategory.ProviderRejected,
				ProviderCode = "NOTFOUND",
			},
			[]
		);

	private static void SetImmutableIdPreference(RequestInformation request) =>
		request.Headers.Add("Prefer", "IdType=\"ImmutableId\"");

	private static async Task<string> MovedProviderIdAsync(
		System.Net.Http.HttpResponseMessage response,
		CancellationToken ct
	)
	{
		var json = await response.Content.ReadAsStringAsync(ct);
		using var document = System.Text.Json.JsonDocument.Parse(json);
		if (!document.RootElement.TryGetProperty("id", out var idProperty))
		{
			throw new InvalidOperationException("Graph move returned no immutable id.");
		}

		return idProperty.GetString()
			?? throw new InvalidOperationException("Graph move returned an empty immutable id.");
	}

	private static TimeSpan? LongestRetryAfter(
		TimeSpan? current,
		System.Net.Http.HttpResponseMessage response
	)
	{
		if (response.StatusCode != System.Net.HttpStatusCode.TooManyRequests)
		{
			return current;
		}

		var header = response.Headers.RetryAfter;
		var delay = header?.Delta
			?? (header?.Date is { } date ? date - DateTimeOffset.UtcNow : (TimeSpan?)null);
		if (delay is not { } positiveDelay || positiveDelay <= TimeSpan.Zero)
		{
			positiveDelay = DefaultRetryAfter;
		}
		return current is null || positiveDelay > current ? positiveDelay : current;
	}

	private static void ThrowIfBatchThrottled(TimeSpan? retryAfter)
	{
		if (retryAfter is { } delay)
		{
			throw new ProviderThrottledException(delay, "Microsoft Graph throttled this request.");
		}
	}

	private static void ThrowIfBatchResponseRequiresRecovery(System.Net.HttpStatusCode status)
	{
		if (status is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
		{
			throw new ProviderAuthenticationException(
				"Microsoft Graph rejected authentication for a dispatched mutation item."
			);
		}

		if (
			status == System.Net.HttpStatusCode.RequestTimeout
			|| (int)status < 200
			|| (int)status is >= 300 and < 400
			|| (int)status >= 500
		)
		{
			throw new HttpRequestException(
				$"Microsoft Graph returned indeterminate HTTP {(int)status} for a dispatched mutation item."
			);
		}
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
				new MutationProblemDetails
				{
					Title = "Graph mutation was rejected",
					Detail = $"Microsoft Graph returned HTTP {(int)status} for this item.",
					Category = ErrorCategory.ProviderRejected,
					ProviderCode = ((int)status).ToString(),
				},
				[]
			);
}
