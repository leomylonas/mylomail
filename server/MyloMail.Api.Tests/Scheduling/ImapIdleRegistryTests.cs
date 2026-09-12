using System.Net.Sockets;
using Hangfire;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MyloMail.Api.Domain;
using MyloMail.Api.Persistence;
using MyloMail.Api.Providers;
using MyloMail.Api.Scheduling;
using MyloMail.Api.Tests.Fakes;
using MyloMail.Api.Tests.Sync;
using Xunit;

namespace MyloMail.Api.Tests.Scheduling;

public sealed class ImapIdleRegistryTests
{
	[Fact]
	public void Active_mailboxes_are_owned_independently_by_each_connection()
	{
		var registry = new ImapIdleRegistry();
		var accountId = Guid.NewGuid();
		var firstMailboxId = Guid.NewGuid();
		var secondMailboxId = Guid.NewGuid();
		registry.SetActiveMailbox("window-1", accountId, firstMailboxId);
		registry.SetActiveMailbox("window-2", accountId, secondMailboxId);
		var selections = registry.Snapshot();
		Assert.Equal(2, selections.Count);
		Assert.Contains((accountId, firstMailboxId), selections);
		Assert.Contains((accountId, secondMailboxId), selections);

		var replacementAccountId = Guid.NewGuid();
		var replacementMailboxId = Guid.NewGuid();
		registry.SetActiveMailbox("window-1", replacementAccountId, replacementMailboxId);
		registry.RemoveConnection("window-2");

		Assert.Equal(
			(replacementAccountId, replacementMailboxId),
			Assert.Single(registry.Snapshot())
		);
	}
	[Fact]
	public async Task Effective_inbox_override_is_always_selected_for_IDLE()
	{
		await using var harness = await SyncHarness.CreateAsync(
			ProviderShapes.Imap(ImapCapabilityTier.Basic)
		);
		var mailboxId = Guid.Parse("00000000-0000-0000-0000-000000000001");
		var secondInboxId = Guid.Parse("00000000-0000-0000-0000-000000000002");
		await harness.UsingAsync(async provider =>
		{
			var context = provider.GetRequiredService<MyloMailDbContext>();
			context.Mailboxes.AddRange(
				new Mailbox
				{
					Id = mailboxId,
					AccountId = harness.Account.Id,
					ProviderMailboxId = "INBOX",
					Name = "Inbox",
					SpecialUse = SpecialUse.None,
					SpecialUseOverride = SpecialUse.Inbox,
					IsSubscribed = true,
				},
				new Mailbox
				{
					Id = secondInboxId,
					AccountId = harness.Account.Id,
					ProviderMailboxId = "Other",
					Name = "Other",
					SpecialUse = SpecialUse.None,
					SpecialUseOverride = SpecialUse.Inbox,
					IsSubscribed = true,
				}
			);
			await context.SaveChangesAsync();
		});

		await harness.UsingAsync(async provider =>
		{
			provider.GetRequiredService<ImapIdleRegistry>()
				.SetActiveMailbox("window", harness.Account.Id, secondInboxId);
			var worker = provider.GetServices<IHostedService>().OfType<ImapIdleWorker>().Single();
			var wanted = await worker.WantedScopesAsync(default);
			Assert.Equal(2, wanted.Count);
			Assert.Contains((harness.Account.Id, mailboxId), wanted);
			Assert.Contains((harness.Account.Id, secondInboxId), wanted);
		});
	}
	[Fact]
	public void Wake_hints_coalesce_to_one_pending_job_and_one_dirty_rerun()
	{
		var registry = new ImapIdleWakeRegistry();
		var scope = (Guid.NewGuid(), Guid.NewGuid());

		Assert.True(registry.Request(scope));
		Assert.False(registry.Request(scope));
		Assert.False(registry.Request(scope));
		Assert.True(registry.Complete(scope));

		Assert.False(registry.Request(scope));
		Assert.True(registry.Complete(scope));
		Assert.False(registry.Complete(scope));
		Assert.True(registry.Request(scope));

		registry.Release(scope);
		Assert.True(registry.Request(scope));
	}

	[Fact]
	public async Task A_failed_IDLE_hint_does_not_start_a_second_owned_poll_loop_on_recovery()
	{
		await using var harness = await SyncHarness.CreateAsync(
			ProviderShapes.Imap(ImapCapabilityTier.Basic)
		);
		harness.Provider.AddMailbox("INBOX", SpecialUse.Inbox);
		var mailboxId = Guid.NewGuid();
		await harness.UsingAsync(async provider =>
		{
			var context = provider.GetRequiredService<MyloMailDbContext>();
			context.Mailboxes.Add(new Mailbox
			{
				Id = mailboxId,
				AccountId = harness.Account.Id,
				ProviderMailboxId = "INBOX",
				Name = "Inbox",
				SpecialUse = SpecialUse.Inbox,
				IsSubscribed = true,
			});
			await context.SaveChangesAsync();
			Assert.True(
				provider
					.GetRequiredService<ImapIdleWakeRegistry>()
					.Request((harness.Account.Id, mailboxId))
			);
		});
		harness.Provider.FailNextChangeStreamWith(new SocketException());

		await harness.UsingAsync(provider =>
			provider
				.GetRequiredService<SyncJobs>()
				.WakeChangeStreamAsync(harness.Account.Id, mailboxId)
		);
		Assert.False(
			await harness.UsingAsync(provider =>
				Task.FromResult(provider.GetRequiredService<ConnectivityMonitor>().IsOnline)
			)
		);
		await harness.UsingAsync(provider =>
			provider.GetRequiredService<ConnectivityMonitor>().ReportAsync(true)
		);

		var jobs = await harness.UsingAsync(provider =>
			Task.FromResult(
				((RecordingJobClient)provider.GetRequiredService<IBackgroundJobClient>()).Created
			)
		);
		Assert.DoesNotContain(
			jobs,
			job => job.Method.Name == nameof(SyncJobs.ChangeStreamAsync)
		);
	}

	[Fact]
	public async Task Stopping_worker_awaits_active_IDLE_session_cleanup()
	{
		await using var harness = await SyncHarness.CreateAsync(
			ProviderShapes.Imap(ImapCapabilityTier.Basic)
		);
		harness.Provider.IdleCancellationDelay = TimeSpan.FromMilliseconds(100);
		await harness.UsingAsync(async provider =>
		{
			var context = provider.GetRequiredService<MyloMailDbContext>();
			context.Mailboxes.Add(new Mailbox
			{
				Id = Guid.NewGuid(),
				AccountId = harness.Account.Id,
				ProviderMailboxId = "INBOX",
				Name = "Inbox",
				SpecialUse = SpecialUse.Inbox,
				IsSubscribed = true,
			});
			await context.SaveChangesAsync();
		});
		var worker = await harness.UsingAsync(provider =>
			Task.FromResult(provider.GetServices<IHostedService>()
				.OfType<ImapIdleWorker>()
				.Single()));
		await worker.StartAsync(default);
		await harness.Provider.IdleStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

		var stopping = worker.StopAsync(default);
		await harness.Provider.IdleCancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
		Assert.False(stopping.IsCompleted);
		await stopping.WaitAsync(TimeSpan.FromSeconds(5));
		Assert.True(harness.Provider.IdleFinished.Task.IsCompleted);
	}


}
