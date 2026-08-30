using Microsoft.Graph;
using Microsoft.Graph.Me.Messages.Item.Move;
using Microsoft.Graph.Models;
using Microsoft.Kiota.Abstractions;
using MyloMail.Api.Domain;
using MyloMail.Api.Errors;
using MyloMail.Api.Providers.Contracts;
using DomainMailbox = MyloMail.Api.Domain.Mailbox;

namespace MyloMail.Api.Providers.Graph;

public sealed partial class GraphMailProvider
{
	public async Task<RawMessageResult> FetchRawMessageAsync(
		Account account,
		MessageOccurrenceRef occurrence,
		CancellationToken ct
	)
	{
		var client = await ClientAsync(account, ct);
		await using var stream = await client.Me.Messages[occurrence.ProviderOccurrenceId].Content.GetAsync(
			null,
			ct
		);
		if (stream is null)
		{
			throw new InvalidOperationException("Graph returned no raw MIME stream.");
		}

		using var buffer = new MemoryStream();
		await stream.CopyToAsync(buffer, ct);
		return new RawMessageResult(buffer.ToArray());
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
		var items = new List<BatchItemResult>(refs.Count);
		foreach (var group in refs.Chunk(20))
		{
			var batch = new BatchRequestContentCollection(client.RequestAdapter, 20);
			var steps = new Dictionary<string, MessageOccurrenceRef>();
			foreach (var reference in group)
			{
				var message = new Message();
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

			var response = await client.Batch.PostAsync(batch, ct);
			var statuses = await response.GetResponsesStatusCodesAsync();
			foreach (var (id, reference) in steps)
			{
				var status = statuses[id];
				items.Add(
					status is >= System.Net.HttpStatusCode.OK and < System.Net.HttpStatusCode.MultipleChoices
						? new BatchItemResult(reference.MessageId, reference.MailboxId, true, null, [])
						: Failed(reference, status)
				);
			}
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

			var response = await client.Batch.PostAsync(batch, ct);
			foreach (var (id, reference) in steps)
			{
				using var itemResponse = await response.GetResponseByIdAsync(id);
				if (!itemResponse.IsSuccessStatusCode)
				{
					items.Add(Failed(reference, itemResponse.StatusCode));
					continue;
				}

				var json = await itemResponse.Content.ReadAsStringAsync(ct);
				using var document = System.Text.Json.JsonDocument.Parse(json);
				if (!document.RootElement.TryGetProperty("id", out var idProperty))
				{
					throw new InvalidOperationException("Graph move returned no immutable id.");
				}

				var providerId = idProperty.GetString()
					?? throw new InvalidOperationException("Graph move returned an empty immutable id.");
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
		}

		return new BatchResult(items);
	}

	public Task<BatchResult> MoveToTrashAsync(
		Account account,
		IReadOnlyList<MessageOccurrenceRef> refs,
		CancellationToken ct
	) => throw new NotSupportedException(NotThinStage);

	public async Task<BatchResult> DeletePermanentlyAsync(
		Account account,
		IReadOnlyList<MessageOccurrenceRef> refs,
		CancellationToken ct
	)
	{
		var client = await ClientAsync(account, ct);
		var items = new List<BatchItemResult>(refs.Count);
		foreach (var reference in refs)
		{
			try
			{
				await client.Me.Messages[reference.ProviderOccurrenceId].DeleteAsync(null, ct);
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
			catch (Microsoft.Kiota.Abstractions.ApiException ex) when (ex.ResponseStatusCode == 404)
			{
				items.Add(NotFound(reference));
			}
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
