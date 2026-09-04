using Microsoft.AspNetCore.SignalR;
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
/// Hundred-and-fourteenth architecture-review pass: <see cref="MailHub.SetFlags"/> and its
/// sibling bulk-action hub methods looped over a multi-select and awaited each enqueue in turn
/// with no per-item try/catch — one message rejected (e.g. pass 72's cross-account ownership
/// check) aborted the whole call, silently leaving every message after it in the same
/// selection untouched, the identical shape pass 113 fixed for dropped attachments.
/// </summary>
public sealed class BulkMessageActionTests
{
	[Fact]
	public async Task A_bulk_flag_change_still_enqueues_the_valid_messages_after_a_rejected_one()
	{
		await using var harness = await SyncHarness.CreateAsync(ProviderShapes.Gmail);
		Guid accountId = default;
		var goodMessageId = Guid.NewGuid();
		var badMessageId = Guid.NewGuid();
		var laterGoodMessageId = Guid.NewGuid();

		await harness.UsingAsync(async services =>
		{
			var context = services.GetRequiredService<MyloMailDbContext>();
			var account = await harness.AccountInScopeAsync(services);
			accountId = account.Id;

			var otherAccountId = Guid.NewGuid();
			context.Accounts.Add(
				new Account
				{
					Id = otherAccountId,
					DisplayName = "Other",
					ProviderType = ProviderType.Gmail,
				}
			);
			context.Messages.Add(new Message { Id = goodMessageId, AccountId = accountId });
			context.Messages.Add(new Message { Id = badMessageId, AccountId = otherAccountId });
			context.Messages.Add(new Message { Id = laterGoodMessageId, AccountId = accountId });
			await context.SaveChangesAsync();
		});

		await harness.UsingAsync(async services =>
		{
			var hub = services.GetRequiredService<MailHub>();
			await Assert.ThrowsAsync<HubException>(
				() =>
					hub.SetFlags(
						accountId,
						[goodMessageId, badMessageId, laterGoodMessageId],
						isRead: true,
						isFlagged: null
					)
			);
		});

		await harness.UsingAsync(async services =>
		{
			var context = services.GetRequiredService<MyloMailDbContext>();
			var enqueuedIds = await context.MutationItems.Select(m => m.MessageId).ToListAsync();
			Assert.Contains(goodMessageId, enqueuedIds);
			// The whole point of this test: a message listed AFTER the rejected one must still
			// be attempted, not silently skipped because the loop stopped early.
			Assert.Contains(laterGoodMessageId, enqueuedIds);
			Assert.DoesNotContain(badMessageId, enqueuedIds);
		});
	}
}
