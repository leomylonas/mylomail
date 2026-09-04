using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
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
	[Fact]
	public async Task Two_pages_are_disjoint_and_together_cover_every_message_in_order()
	{
		await using var harness = await SyncHarness.CreateAsync(ProviderShapes.Gmail);
		var mailboxId = await SeedMessagesAsync(harness, count: 150);

		var (firstPage, secondPage) = await harness.UsingAsync(async services =>
		{
			var hub = services.GetRequiredService<MailHub>();
			var first = await hub.GetMessages(mailboxId, skip: 0, take: 100);
			var second = await hub.GetMessages(mailboxId, skip: 100, take: 100);
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
	public async Task A_page_past_the_end_is_empty_not_an_error()
	{
		await using var harness = await SyncHarness.CreateAsync(ProviderShapes.Gmail);
		var mailboxId = await SeedMessagesAsync(harness, count: 10);

		var page = await harness.UsingAsync(services =>
			services.GetRequiredService<MailHub>().GetMessages(mailboxId, skip: 100, take: 100)
		);

		Assert.Empty(page);
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
