using System.Net;
using Google.Apis.Gmail.v1.Data;
using MyloMail.Api.Providers;
using MyloMail.Api.Providers.Contracts;
using MyloMail.Api.Providers.Gmail;
using Xunit;

namespace MyloMail.Api.Tests.Providers;

public sealed class GmailMailProviderHistoryTests
{
	[Fact]
	public void Permanent_history_deletion_is_expressed_without_a_hydration_request()
	{
		var changes = GmailMailProvider.GmailHistoryChanges.From(
		[
			new History
			{
				MessagesDeleted =
				[
					new HistoryMessageDeleted { Message = new Message { Id = "deleted" } },
				],
			},
		]
		);

		Assert.Equal(["deleted"], changes.PermanentlyDeletedMessageIds);
		Assert.Empty(changes.ChangedMessageIds);
		Assert.Empty(changes.LabelRemovals);
	}

	[Fact]
	public void Readded_label_wins_over_an_earlier_removal_in_the_same_page()
	{
		var changes = GmailMailProvider.GmailHistoryChanges.From(
		[
			new History
			{
				LabelsRemoved =
				[
					new HistoryLabelRemoved
					{
						Message = new Message { Id = "message" },
						LabelIds = ["label"],
					},
				],
			},
		]
		);
		var current = new MessageDto
		{
			Occurrences = [new MessageOccurrenceDto("label", "message")],
			ReceivedAt = DateTimeOffset.UnixEpoch,
		};

		Assert.Empty(GmailMailProvider.FinalLabelRemovals(changes.LabelRemovals, [current]));
	}

	[Fact]
	public void Label_metadata_keeps_authoritative_counts_from_labels_get()
	{
		var mailbox = GmailMailProvider.ToMailboxDto(
			new Label
			{
				Id = "label",
				Name = "Projects",
				MessagesTotal = 431,
				MessagesUnread = 29,
			}
		);

		Assert.Equal(431, mailbox.TotalCount);
		Assert.Equal(29, mailbox.UnreadCount);
	}

	[Fact]
	public void Batched_preflight_uses_Gmail_batch_limit_and_preserves_partial_outcomes()
	{
		var found = new MessageOccurrenceRef(Guid.NewGuid(), Guid.NewGuid(), "found");
		var missing = new MessageOccurrenceRef(Guid.NewGuid(), Guid.NewGuid(), "missing");

		Assert.Equal(100, GmailMailProvider.MutationPreflightBatchSize);
		var outcomes = GmailMailProvider.ClassifyPreflightOutcomes(
			[found, missing],
			[
				new BatchItemResult(found.MessageId, found.MailboxId, true, null, []),
				GmailMailProvider.Failed(missing, HttpStatusCode.NotFound),
			]
		);

		Assert.Equal([found], outcomes.Existing);
		var outcome = Assert.Single(outcomes.Missing);
		Assert.Equal(missing.MessageId, outcome.MessageId);
		Assert.Equal(missing.MailboxId, outcome.MailboxId);
		Assert.Equal("NOTFOUND", outcome.Problem?.ProviderCode);
	}
	[Theory]
	[InlineData(HttpStatusCode.Unauthorized)]
	[InlineData(HttpStatusCode.Forbidden)]
	public void Authentication_preflight_responses_are_not_terminal_missing_items(HttpStatusCode status)
	{
		var reference = new MessageOccurrenceRef(Guid.NewGuid(), Guid.NewGuid(), "message");

		Assert.Throws<ProviderAuthenticationException>(() =>
			GmailMailProvider.PreflightOutcome(reference, status)
		);
	}

	[Theory]
	[InlineData(HttpStatusCode.RequestTimeout)]
	[InlineData(HttpStatusCode.ServiceUnavailable)]
	public void Indeterminate_preflight_responses_are_not_terminal_missing_items(HttpStatusCode status)
	{
		var reference = new MessageOccurrenceRef(Guid.NewGuid(), Guid.NewGuid(), "message");

		Assert.Throws<HttpRequestException>(() => GmailMailProvider.PreflightOutcome(reference, status));
	}

	[Fact]
	public void Only_a_not_found_preflight_response_is_mapped_to_a_missing_item()
	{
		var reference = new MessageOccurrenceRef(Guid.NewGuid(), Guid.NewGuid(), "message");

		var missing = GmailMailProvider.PreflightOutcome(reference, HttpStatusCode.NotFound);
		var rejected = GmailMailProvider.PreflightOutcome(reference, HttpStatusCode.BadRequest);

		Assert.Equal("NOTFOUND", missing.Problem?.ProviderCode);
		Assert.Equal("400", rejected.Problem?.ProviderCode);
	}

}
