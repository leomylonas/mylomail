using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MyloMail.Api.Contracts;
using MyloMail.Api.Domain;
using MyloMail.Api.Hubs;
using MyloMail.Api.Persistence;
using MyloMail.Api.Tests.Fakes;
using MyloMail.Api.Tests.Sync;
using Xunit;

namespace MyloMail.Api.Tests.Hubs;

/// <summary>
/// Eighty-fifth architecture-review pass: <see cref="MailHub.GetMessages"/> had no pagination
/// at all — every caller got the same flat top-100, with nothing beyond it ever reachable in
/// the UI. Now paged by <c>skip</c>/<c>take</c>, ordered by <c>ReceivedAt</c> descending with
/// <c>Id</c> as a tiebreaker so two pages fetched moments apart, over an unchanged mailbox,
/// partition the same total order rather than reshuffling ties between calls.
/// </summary>
public sealed class MessageListPagingTests
{
	private static readonly MessageListFilterDto NoFilter = new(null, null, null, null, null);

	[Fact]
	public async Task Two_pages_are_disjoint_and_together_cover_every_message_in_order()
	{
		await using var harness = await SyncHarness.CreateAsync(ProviderShapes.Gmail);
		var mailboxId = await SeedMessagesAsync(harness, count: 150);

		var (firstPage, secondPage) = await harness.UsingAsync(async services =>
		{
			var hub = services.GetRequiredService<MailHub>();
			var first = await hub.GetMessages(mailboxId, skip: 0, take: 100, MessageSortField.Date, descending: true, NoFilter);
			var second = await hub.GetMessages(mailboxId, skip: 100, take: 100, MessageSortField.Date, descending: true, NoFilter);
			return (first, second);
		});

		Assert.Equal(100, firstPage.Count);
		Assert.Equal(50, secondPage.Count);

		var firstIds = firstPage.Select(m => m.Id).ToList();
		var secondIds = secondPage.Select(m => m.Id).ToList();
		Assert.Empty(firstIds.Intersect(secondIds));
		Assert.Equal(150, firstIds.Concat(secondIds).Distinct().Count());

		var combinedOrder = firstPage.Concat(secondPage).Select(m => m.ReceivedAt).ToList();
		Assert.Equal(combinedOrder.OrderByDescending(t => t), combinedOrder);
	}

	[Fact]
	public async Task A_page_reports_and_loads_thread_members_beyond_its_boundary()
	{
		await using var harness = await SyncHarness.CreateAsync(ProviderShapes.Gmail);
		var mailboxId = await SeedMessagesAsync(harness, count: 100);
		const string threadId = "local:complete-thread";
		await harness.UsingAsync(async services =>
		{
			var context = services.GetRequiredService<MyloMailDbContext>();
			var account = await harness.AccountInScopeAsync(services);
			foreach (var receivedAt in new[]
			{
				DateTimeOffset.UnixEpoch.AddDays(2),
				DateTimeOffset.UnixEpoch.AddDays(-2),
			})
			{
				var message = new Message
				{
					Id = Guid.NewGuid(),
					AccountId = account.Id,
					ThreadId = threadId,
					ReceivedAt = receivedAt,
				};
				context.Messages.Add(message);
				context.MessageMailboxes.Add(new MessageMailbox
				{
					Id = Guid.NewGuid(),
					MessageId = message.Id,
					MailboxId = mailboxId,
					ProviderOccurrenceId = $"thread-{receivedAt.Ticks}",
				});
			}
			await context.SaveChangesAsync();
		});

		var (representative, members) = await harness.UsingAsync(async services =>
		{
			var hub = services.GetRequiredService<MailHub>();
			var page = await hub.GetMessages(mailboxId, skip: 0, take: 100, MessageSortField.Date, descending: true, NoFilter);
			return (
				page.Single(message => message.ThreadId == threadId),
				await hub.GetThreadMessages(mailboxId, threadId)
			);
		});

		Assert.Equal(2, representative.ThreadMessageCount);
		Assert.Equal(2, members.Count);
	}

	[Fact]
	public async Task A_page_past_the_end_is_empty_not_an_error()
	{
		await using var harness = await SyncHarness.CreateAsync(ProviderShapes.Gmail);
		var mailboxId = await SeedMessagesAsync(harness, count: 10);

		var page = await harness.UsingAsync(services =>
			services.GetRequiredService<MailHub>().GetMessages(mailboxId, skip: 100, take: 100, MessageSortField.Date, descending: true, NoFilter)
		);

		Assert.Empty(page);
	}

	/// <summary>
	/// Sorting a list that pages must order the whole mailbox before taking a page. Ordering only
	/// the pages loaded so far makes "oldest first" mean the oldest of the current window.
	/// </summary>
	[Fact]
	public async Task Sorting_orders_the_whole_mailbox_before_paging()
	{
		await using var harness = await SyncHarness.CreateAsync(ProviderShapes.Gmail);
		var mailboxId = Guid.NewGuid();
		await harness.UsingAsync(async services =>
		{
			var context = services.GetRequiredService<MyloMailDbContext>();
			context.Mailboxes.Add(new Mailbox { Id = mailboxId, AccountId = harness.Account.Id, ProviderMailboxId = "INBOX", Name = "Inbox", SpecialUse = SpecialUse.Inbox });
			// Stored newest-first by id order is irrelevant; dates are shuffled against subjects.
			for (var i = 0; i < 250; i++)
			{
				var id = Guid.NewGuid();
				context.Messages.Add(new Message
				{
					Id = id,
					AccountId = harness.Account.Id,
					Subject = $"Subject {(i * 37) % 250:D3}",
					ReceivedAt = DateTimeOffset.UnixEpoch.AddHours((i * 91) % 250),
				});
				context.MessageMailboxes.Add(new MessageMailbox { Id = Guid.NewGuid(), MessageId = id, MailboxId = mailboxId, ProviderOccurrenceId = id.ToString() });
			}
			await context.SaveChangesAsync();
		});

		async Task<List<MessageSummaryDto>> AllPagesAsync(MessageSortField field, bool descending) =>
			await harness.UsingAsync(async services =>
			{
				var hub = services.GetRequiredService<MailHub>();
				var all = new List<MessageSummaryDto>();
				for (var skip = 0; skip < 250; skip += 100)
					all.AddRange(await hub.GetMessages(mailboxId, skip, 100, field, descending, NoFilter));
				return all;
			});

		var oldestFirst = await AllPagesAsync(MessageSortField.Date, descending: false);
		Assert.Equal(250, oldestFirst.Select(m => m.Id).Distinct().Count());
		Assert.Equal(DateTimeOffset.UnixEpoch, oldestFirst[0].ReceivedAt);
		Assert.Equal(oldestFirst.OrderBy(m => m.ReceivedAt).Select(m => m.Id), oldestFirst.Select(m => m.Id));

		var newestFirst = await AllPagesAsync(MessageSortField.Date, descending: true);
		Assert.Equal(DateTimeOffset.UnixEpoch.AddHours(249), newestFirst[0].ReceivedAt);

		var bySubject = await AllPagesAsync(MessageSortField.Subject, descending: false);
		Assert.Equal("Subject 000", bySubject[0].Subject);
		Assert.Equal("Subject 249", bySubject[^1].Subject);
		Assert.Equal(bySubject.Select(m => m.Subject).Order(StringComparer.CurrentCultureIgnoreCase), bySubject.Select(m => m.Subject));
	}

	/// <summary>
	/// A filter must narrow the whole mailbox, not the pages loaded so far: a match on the
	/// last page of a long mailbox has to turn up on the first page of the filtered list.
	/// </summary>
	[Fact]
	public async Task Filters_apply_across_the_whole_mailbox_before_paging()
	{
		await using var harness = await SyncHarness.CreateAsync(ProviderShapes.Gmail);
		var mailboxId = Guid.NewGuid();
		var oldUnreadFlagged = Guid.NewGuid();
		await harness.UsingAsync(async services =>
		{
			var context = services.GetRequiredService<MyloMailDbContext>();
			context.Mailboxes.Add(new Mailbox { Id = mailboxId, AccountId = harness.Account.Id, ProviderMailboxId = "INBOX", Name = "Inbox", SpecialUse = SpecialUse.Inbox });
			for (var i = 0; i < 250; i++)
			{
				var id = i == 0 ? oldUnreadFlagged : Guid.NewGuid();
				context.Messages.Add(new Message
				{
					Id = id,
					AccountId = harness.Account.Id,
					Subject = i == 0 ? "Quarterly NEEDLE report" : $"Routine {i}",
					From = [new Address(i == 0 ? "Claire" : "Other", i == 0 ? "claire@example.test" : "other@example.test")],
					// The one that matches is the oldest, so it is on the last page unfiltered.
					ReceivedAt = DateTimeOffset.UnixEpoch.AddHours(i),
					IsRead = i != 0,
					IsFlagged = i == 0,
				});
				context.MessageMailboxes.Add(new MessageMailbox { Id = Guid.NewGuid(), MessageId = id, MailboxId = mailboxId, ProviderOccurrenceId = id.ToString() });
			}
			await context.SaveChangesAsync();
		});

		async Task<List<Guid>> FirstPageAsync(MessageListFilterDto filter) =>
			await harness.UsingAsync(async services =>
				(await services.GetRequiredService<MailHub>().GetMessages(mailboxId, 0, 100, MessageSortField.Date, true, filter)).Select(m => m.Id).ToList());

		Assert.Equal([oldUnreadFlagged], await FirstPageAsync(NoFilter with { Text = "  needle " }));
		Assert.Equal([oldUnreadFlagged], await FirstPageAsync(NoFilter with { Text = "CLAIRE@example" }));
		Assert.Equal([oldUnreadFlagged], await FirstPageAsync(NoFilter with { IsRead = false }));
		Assert.Equal([oldUnreadFlagged], await FirstPageAsync(NoFilter with { IsFlagged = true }));
		Assert.Equal(100, (await FirstPageAsync(NoFilter with { IsFlagged = false })).Count);
		Assert.Equal([oldUnreadFlagged], await FirstPageAsync(NoFilter with { ReceivedBefore = DateTimeOffset.UnixEpoch.AddHours(1) }));
		Assert.Empty(await FirstPageAsync(NoFilter with { ReceivedFrom = DateTimeOffset.UnixEpoch.AddDays(30) }));
		Assert.Empty(await FirstPageAsync(NoFilter with { Text = "needle", IsRead = true }));
	}

	private static async Task<Guid> SeedMessagesAsync(SyncHarness harness, int count)
	{
		var mailboxId = Guid.NewGuid();
		await harness.UsingAsync(async services =>
		{
			var context = services.GetRequiredService<MyloMailDbContext>();
			var account = await harness.AccountInScopeAsync(services);
			context.Mailboxes.Add(
				new Mailbox
				{
					Id = mailboxId,
					AccountId = account.Id,
					ProviderMailboxId = "INBOX",
					Name = "Inbox",
					SpecialUse = SpecialUse.Inbox,
				}
			);

			var baseTime = DateTimeOffset.UnixEpoch;
			for (var i = 0; i < count; i++)
			{
				var messageId = Guid.NewGuid();
				context.Messages.Add(
					new Message
					{
						Id = messageId,
						AccountId = account.Id,
						// Every message shares one timestamp deliberately: the whole point of
						// this fixture is to exercise the Id tiebreaker, not to test ordinary
						// ReceivedAt ordering (already covered elsewhere).
						ReceivedAt = baseTime,
					}
				);
				context.MessageMailboxes.Add(
					new MessageMailbox
					{
						Id = Guid.NewGuid(),
						MessageId = messageId,
						MailboxId = mailboxId,
						ProviderOccurrenceId = $"UID-{i}",
					}
				);
			}
			await context.SaveChangesAsync();
		});
		return mailboxId;
	}
}
