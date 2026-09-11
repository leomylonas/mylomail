using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MyloMail.Api.Contacts;
using MyloMail.Api.Domain;
using MyloMail.Api.FaultInjection;
using MyloMail.Api.Persistence;
using MyloMail.Api.Providers;
using MyloMail.Api.Providers.Contracts;
using MyloMail.Api.Scheduling;
using MyloMail.Api.Sync;
using MyloMail.Api.Tests.Fakes;
using Xunit;

namespace MyloMail.Api.Tests.Sync;

public sealed class MessageThreadingTests
{
	[Fact]
	public async Task References_resolves_a_page_local_parent_regardless_of_provider_order()
	{
		await using var harness = await SyncHarness.CreateAsync(ProviderShapes.Gmail);
		await harness.UsingAsync(async provider =>
		{
			var context = provider.GetRequiredService<MyloMailDbContext>();
			var account = await harness.AccountInScopeAsync(provider);
			var mailbox = new Mailbox
			{
				Id = Guid.NewGuid(),
				AccountId = account.Id,
				ProviderMailboxId = "INBOX",
				Name = "Inbox",
				SpecialUse = SpecialUse.Inbox,
				IsSubscribed = true,
			};
			context.Mailboxes.Add(mailbox);
			await context.SaveChangesAsync();
			var ingestor = new MessageIngestor(context, new ContactSuggestionService(context));
			var messages = new[]
			{
				Message("grandchild", "<grandchild@example.test>", "<child@example.test>"),
				Message("child", "<child@example.test>", "<parent@example.test>"),
				Message("parent", "<parent@example.test>", null),
			};

			await ingestor.IngestAsync(
				account,
				messages,
				new Dictionary<string, Mailbox> { ["INBOX"] = mailbox },
				GenerationSnapshot.Capture([mailbox])
			);
			await context.SaveChangesAsync();
			var stored = (await context.Messages.ToListAsync()).OrderBy(message => message.ReceivedAt).ToList();
			Assert.Equal(3, stored.Count);
			Assert.Single(stored.Select(message => message.ThreadId).Distinct());
		});
	}
	[Fact]
	public async Task Later_page_ancestors_rethread_already_stored_descendants()
	{
		await using var harness = await SyncHarness.CreateAsync(ProviderShapes.Gmail);
		await harness.UsingAsync(async provider =>
		{
			var context = provider.GetRequiredService<MyloMailDbContext>();
			var account = await harness.AccountInScopeAsync(provider);
			var mailbox = new Mailbox
			{
				Id = Guid.NewGuid(),
				AccountId = account.Id,
				ProviderMailboxId = "INBOX",
				Name = "Inbox",
				SpecialUse = SpecialUse.Inbox,
				IsSubscribed = true,
			};
			context.Mailboxes.Add(mailbox);
			await context.SaveChangesAsync();
			var ingestor = new MessageIngestor(context, new ContactSuggestionService(context));
			var mailboxes = new Dictionary<string, Mailbox> { ["INBOX"] = mailbox };
			var generations = GenerationSnapshot.Capture([mailbox]);

			await ingestor.IngestAsync(
				account,
				[Message("grandchild", "<grandchild@example.test>", "<child@example.test>")],
				mailboxes,
				generations
			);
			await context.SaveChangesAsync();
			var result = await ingestor.IngestAsync(
				account,
				[
					Message("grandchild", "<grandchild@example.test>", "<child@example.test>"),
					Message("child", "<child@example.test>", "<parent@example.test>"),
					Message("parent", "<parent@example.test>", null),
				],
				mailboxes,
				generations
			);
			await context.SaveChangesAsync();
			Assert.Contains(result.Rethreaded, message =>
				message.ProviderStableId == "grandchild");

			var threads = await context.Messages.Select(message => message.ThreadId).ToListAsync();
			Assert.Equal(3, threads.Count);
			Assert.Single(threads.Distinct());
		});
	}

	[Fact]
	public async Task A_later_descendant_reuses_the_persisted_fallback_ancestor_root()
	{
		await using var harness = await SyncHarness.CreateAsync(ProviderShapes.Imap(ImapCapabilityTier.Basic));
		await harness.UsingAsync(async provider =>
		{
			var context = provider.GetRequiredService<MyloMailDbContext>();
			var account = await harness.AccountInScopeAsync(provider);
			var mailbox = new Mailbox
			{
				Id = Guid.NewGuid(),
				AccountId = account.Id,
				ProviderMailboxId = "INBOX",
				Name = "Inbox",
				SpecialUse = SpecialUse.Inbox,
				IsSubscribed = true,
			};
			context.Mailboxes.Add(mailbox);
			await context.SaveChangesAsync();
			var ingestor = new MessageIngestor(context, new ContactSuggestionService(context));
			var mailboxes = new Dictionary<string, Mailbox> { ["INBOX"] = mailbox };
			var generations = GenerationSnapshot.Capture([mailbox]);

			await ingestor.IngestAsync(
				account,
				[
					Message("grandparent", "<grandparent@example.test>", null),
					Message("parent", "<parent@example.test>", "<grandparent@example.test>"),
				],
				mailboxes,
				generations
			);
			await context.SaveChangesAsync();
			await ingestor.IngestAsync(
				account,
				[Message("child", "<child@example.test>", "<parent@example.test>")],
				mailboxes,
				generations
			);
			await context.SaveChangesAsync();

			var stored = await context.Messages.ToListAsync();
			Assert.Equal(3, stored.Count);
			Assert.Single(stored.Select(message => message.ThreadId).Distinct());
		});
	}
	[Fact]
	public async Task A_new_ancestor_rethreads_its_persisted_parent_before_a_new_child_is_assigned()
	{
		await using var harness = await SyncHarness.CreateAsync(
			ProviderShapes.Imap(ImapCapabilityTier.Basic)
		);
		await harness.UsingAsync(async provider =>
		{
			var context = provider.GetRequiredService<MyloMailDbContext>();
			var account = await harness.AccountInScopeAsync(provider);
			var mailbox = new Mailbox
			{
				Id = Guid.NewGuid(),
				AccountId = account.Id,
				ProviderMailboxId = "INBOX",
				Name = "Inbox",
				SpecialUse = SpecialUse.Inbox,
				IsSubscribed = true,
			};
			context.Mailboxes.Add(mailbox);
			await context.SaveChangesAsync();
			var ingestor = new MessageIngestor(context, new ContactSuggestionService(context));
			var mailboxes = new Dictionary<string, Mailbox> { ["INBOX"] = mailbox };
			var generations = GenerationSnapshot.Capture([mailbox]);
			await ingestor.IngestAsync(
				account,
				[Message("parent", "<parent@example.test>", "<grandparent@example.test>")],
				mailboxes,
				generations
			);
			await context.SaveChangesAsync();

			await ingestor.IngestAsync(
				account,
				[
					Message("grandparent", "<grandparent@example.test>", null),
					Message("child", "<child@example.test>", "<parent@example.test>"),
				],
				mailboxes,
				generations
			);
			await context.SaveChangesAsync();

			var stored = await context.Messages.ToListAsync();
			Assert.Equal(3, stored.Count);
			Assert.Single(stored.Select(message => message.ThreadId).Distinct());
		});
	}


	[Fact]
	public async Task Repeated_provider_rows_snapshot_one_local_thread_and_apply_the_last_observation()
	{
		await using var harness = await SyncHarness.CreateAsync(ProviderShapes.Graph);
		await harness.UsingAsync(async provider =>
		{
			var context = provider.GetRequiredService<MyloMailDbContext>();
			var account = await harness.AccountInScopeAsync(provider);
			var mailbox = new Mailbox
			{
				Id = Guid.NewGuid(),
				AccountId = account.Id,
				ProviderMailboxId = "INBOX",
				Name = "Inbox",
				SpecialUse = SpecialUse.Inbox,
				IsSubscribed = true,
			};
			context.Mailboxes.Add(mailbox);
			await context.SaveChangesAsync();
			var ingestor = new MessageIngestor(context, new ContactSuggestionService(context));
			var mailboxes = new Dictionary<string, Mailbox> { ["INBOX"] = mailbox };
			var generations = GenerationSnapshot.Capture([mailbox]);
			await ingestor.IngestAsync(
				account,
				[Message("message", "<message@example.test>", null)],
				mailboxes,
				generations
			);
			await context.SaveChangesAsync();
			var first = Message("message", "<message@example.test>", null) with
			{
				Subject = "Earlier delta row",
			};
			var last = Message("message", "<message@example.test>", null) with
			{
				Subject = "Latest delta row",
			};

			await ingestor.IngestAsync(account, [first, last], mailboxes, generations);
			await context.SaveChangesAsync();

			Assert.Equal("Latest delta row", (await context.Messages.SingleAsync()).Subject);
		});
	}
	[Fact]
	public async Task A_repeated_row_cannot_leave_its_superseded_header_as_a_parent_candidate()
	{
		await using var harness = await SyncHarness.CreateAsync(ProviderShapes.Graph);
		await harness.UsingAsync(async provider =>
		{
			var context = provider.GetRequiredService<MyloMailDbContext>();
			var account = await harness.AccountInScopeAsync(provider);
			var mailbox = new Mailbox
			{
				Id = Guid.NewGuid(),
				AccountId = account.Id,
				ProviderMailboxId = "INBOX",
				Name = "Inbox",
				SpecialUse = SpecialUse.Inbox,
				IsSubscribed = true,
			};
			context.Mailboxes.Add(mailbox);
			await context.SaveChangesAsync();
			var ingestor = new MessageIngestor(context, new ContactSuggestionService(context));
			var mailboxes = new Dictionary<string, Mailbox> { ["INBOX"] = mailbox };
			var generations = GenerationSnapshot.Capture([mailbox]);
			await ingestor.IngestAsync(
				account,
				[Message("parent", "<old@example.test>", null, "provider-thread")],
				mailboxes,
				generations
			);
			await context.SaveChangesAsync();

			await ingestor.IngestAsync(
				account,
				[
					Message("parent", "<old@example.test>", null, "provider-thread"),
					Message("child", "<child@example.test>", "<old@example.test>"),
					Message("parent", "<new@example.test>", null, "provider-thread"),
				],
				mailboxes,
				generations
			);
			await context.SaveChangesAsync();

			var child = await context.Messages.SingleAsync(message =>
				message.ProviderStableId == "child");
			Assert.Equal($"local:{child.Id:N}", child.ThreadId);
		});
	}


	[Fact]
	public async Task Replacing_a_message_id_rethreads_descendants_of_the_old_header()
	{
		await using var harness = await SyncHarness.CreateAsync(ProviderShapes.Graph);
		await harness.UsingAsync(async provider =>
		{
			var context = provider.GetRequiredService<MyloMailDbContext>();
			var account = await harness.AccountInScopeAsync(provider);
			var mailbox = new Mailbox
			{
				Id = Guid.NewGuid(),
				AccountId = account.Id,
				ProviderMailboxId = "INBOX",
				Name = "Inbox",
				SpecialUse = SpecialUse.Inbox,
				IsSubscribed = true,
			};
			context.Mailboxes.Add(mailbox);
			await context.SaveChangesAsync();
			var ingestor = new MessageIngestor(context, new ContactSuggestionService(context));
			var mailboxes = new Dictionary<string, Mailbox> { ["INBOX"] = mailbox };
			var generations = GenerationSnapshot.Capture([mailbox]);
			await ingestor.IngestAsync(
				account,
				[
					Message("parent", "<old@example.test>", null, "provider-thread"),
					Message("child", "<child@example.test>", "<old@example.test>"),
				],
				mailboxes,
				generations
			);
			await context.SaveChangesAsync();

			await ingestor.IngestAsync(
				account,
				[Message("parent", "<new@example.test>", null, "provider-thread")],
				mailboxes,
				generations
			);
			await context.SaveChangesAsync();

			var child = await context.Messages.SingleAsync(message =>
				message.ProviderStableId == "child");
			Assert.Equal($"local:{child.Id:N}", child.ThreadId);
		});
	}


	[Fact]
	public async Task A_late_duplicate_parent_header_splits_an_existing_fallback_thread()
	{
		await using var harness = await SyncHarness.CreateAsync(ProviderShapes.Gmail);
		await harness.UsingAsync(async provider =>
		{
			var context = provider.GetRequiredService<MyloMailDbContext>();
			var account = await harness.AccountInScopeAsync(provider);
			var mailbox = new Mailbox
			{
				Id = Guid.NewGuid(),
				AccountId = account.Id,
				ProviderMailboxId = "INBOX",
				Name = "Inbox",
				SpecialUse = SpecialUse.Inbox,
				IsSubscribed = true,
			};
			context.Mailboxes.Add(mailbox);
			await context.SaveChangesAsync();
			var ingestor = new MessageIngestor(context, new ContactSuggestionService(context));
			var mailboxes = new Dictionary<string, Mailbox> { ["INBOX"] = mailbox };
			var generations = GenerationSnapshot.Capture([mailbox]);

			await ingestor.IngestAsync(
				account,
				[
					Message("parent-1", "<parent@example.test>", null, "provider-thread"),
					Message("child", "<child@example.test>", "<parent@example.test>"),
				],
				mailboxes,
				generations
			);
			await context.SaveChangesAsync();
			var initiallyThreadedChild = await context.Messages.SingleAsync(
				message => message.ProviderStableId == "child"
			);
			Assert.Equal("provider-thread", initiallyThreadedChild.ThreadId);
			Assert.False(initiallyThreadedChild.HasProviderThreadId);
			await ingestor.IngestAsync(
				account,
				[Message("parent-2", "<parent@example.test>", null)],
				mailboxes,
				generations
			);
			await context.SaveChangesAsync();

			var child = await context.Messages.SingleAsync(
				message => message.ProviderStableId == "child"
			);
			Assert.Equal($"local:{child.Id:N}", child.ThreadId);
		});
	}

	[Fact]
	public async Task Backfill_rebuilds_existing_fallback_threads_transitively_and_is_idempotent()
	{
		await using var harness = await SyncHarness.CreateAsync(ProviderShapes.Gmail);
		await harness.UsingAsync(async provider =>
		{
			var context = provider.GetRequiredService<MyloMailDbContext>();
			var account = await harness.AccountInScopeAsync(provider);
			context.Messages.AddRange(
				new Message
				{
					Id = Guid.NewGuid(),
					AccountId = account.Id,
					MessageIdHeader = "<parent@example.test>",
					ThreadId = "provider-thread",
					HasProviderThreadId = true,
				},
				new Message
				{
					Id = Guid.NewGuid(),
					AccountId = account.Id,
					MessageIdHeader = "<child@example.test>",
					ReferencesHeader = "<parent@example.test>",
				},
				new Message
				{
					Id = Guid.NewGuid(),
					AccountId = account.Id,
					MessageIdHeader = "<grandchild@example.test>",
					ReferencesHeader = "<child@example.test>",
				}
			);
			await context.SaveChangesAsync();
			var ingestor = new MessageIngestor(context, new ContactSuggestionService(context));

			await ingestor.RebuildFallbackThreadsAsync(account.Id, default);
			await ingestor.RebuildFallbackThreadsAsync(account.Id, default);

			Assert.All(
				await context.Messages.Where(message => message.AccountId == account.Id).ToListAsync(),
				message => Assert.Equal("provider-thread", message.ThreadId)
			);
		});
	}

	[Trait("Category", "FaultInjection")]
	[Trait("Category", "Deep")]
	[Fact]
	public async Task Startup_thread_backfill_replays_after_a_crash_before_its_completion_marker()
	{
		await using var harness = await SyncHarness.CreateAsync(ProviderShapes.Gmail);
		await harness.UsingAsync(async provider =>
		{
			var context = provider.GetRequiredService<MyloMailDbContext>();
			var secondAccount = new Account
			{
				Id = Guid.NewGuid(),
				DisplayName = "Second",
				ProviderType = ProviderType.Gmail,
			};
			context.Accounts.Add(secondAccount);
			foreach (var accountId in new[] { harness.Account.Id, secondAccount.Id })
			{
				context.Messages.AddRange(
					new Message
					{
						Id = Guid.NewGuid(),
						AccountId = accountId,
						MessageIdHeader = "<parent@example.test>",
						ThreadId = $"provider:{accountId:N}",
						HasProviderThreadId = true,
					},
					new Message
					{
						Id = Guid.NewGuid(),
						AccountId = accountId,
						MessageIdHeader = "<child@example.test>",
						ReferencesHeader = "<parent@example.test>",
					}
				);
			}
			await context.SaveChangesAsync();
		});
		harness.Faults.ArmAt(FaultPoints.MessageThreadBackfillBeforeCompletion);

		await Assert.ThrowsAsync<SimulatedCrashException>(() => harness.UsingAsync(provider =>
			provider.GetRequiredService<StartupScheduler>().ScheduleAsync()));
		await harness.UsingAsync(async provider =>
		{
			var context = provider.GetRequiredService<MyloMailDbContext>();
			Assert.Equal(0, (await context.AppSettings.SingleAsync()).MessageThreadBackfillVersion);
			Assert.Single(await context.Messages.Where(message =>
				message.ReferencesHeader != null && message.ThreadId == null).ToListAsync());
		});

		await harness.RestartAsync();
		await harness.UsingAsync(provider => provider.GetRequiredService<StartupScheduler>().ScheduleAsync());

		await harness.UsingAsync(async provider =>
		{
			var context = provider.GetRequiredService<MyloMailDbContext>();
			Assert.Equal(1, (await context.AppSettings.SingleAsync()).MessageThreadBackfillVersion);
			var children = await context.Messages
				.Where(message => message.ReferencesHeader != null)
				.ToListAsync();
			Assert.Equal(2, children.Count);
			Assert.All(children, child => Assert.Equal($"provider:{child.AccountId:N}", child.ThreadId));
		});
	}

	private static MessageDto Message(
		string id,
		string messageId,
		string? references,
		string? threadId = null
	) => new()
	{
		ProviderStableId = id,
		MessageIdHeader = messageId,
		ReferencesHeader = references,
		ThreadId = threadId,
		ReceivedAt = DateTimeOffset.UnixEpoch.AddMinutes(id switch
		{
			"parent" or "parent-1" => 0,
			"child" => 1,
			"parent-2" => 3,
			_ => 2,
		}),
		Occurrences = [new MessageOccurrenceDto("INBOX", id)],
	};
}
