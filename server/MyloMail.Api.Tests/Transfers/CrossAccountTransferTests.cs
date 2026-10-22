using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MyloMail.Api.Domain;
using MyloMail.Api.FaultInjection;
using MyloMail.Api.Hubs;
using MyloMail.Api.Persistence;
using MyloMail.Api.Providers;
using MyloMail.Api.Tests.Fakes;
using Xunit;

namespace MyloMail.Api.Tests.Transfers;

public sealed class CrossAccountTransferTests
{
	[Fact]
	public async Task A_message_is_copied_with_its_state_and_its_original_queued_for_the_trash()
	{
		await using var harness = await TransferHarness.CreateAsync();
		var message = await harness.AddMessageAsync("<m1@example.test>", isRead: false, isFlagged: true);

		Assert.Equal(1, await harness.EnqueueAsync(message));
		Assert.False(await harness.RunAsync());

		var copy = Assert.Single(harness.TargetProvider.Imported);
		Assert.Equal("INBOX", copy.ProviderMailboxId);
		Assert.Equal(TransferHarness.Raw("<m1@example.test>"), copy.Raw);
		Assert.False(copy.IsRead);
		Assert.True(copy.IsFlagged);
		Assert.Equal(1, await harness.TrashMutationsAsync(message));
		Assert.Equal(CrossAccountTransferState.Completed, Assert.Single(await harness.TransfersAsync()).State);
	}

	[Fact]
	public async Task A_message_already_being_moved_is_not_started_twice()
	{
		await using var harness = await TransferHarness.CreateAsync();
		var message = await harness.AddMessageAsync();

		Assert.Equal(1, await harness.EnqueueAsync(message));
		Assert.Equal(0, await harness.EnqueueAsync(message));
		await harness.RunAsync();

		Assert.Single(harness.TargetProvider.Imported);
	}

	[Fact]
	public async Task Moving_to_the_same_account_is_rejected()
	{
		await using var harness = await TransferHarness.CreateAsync();
		var message = await harness.AddMessageAsync();

		await Assert.ThrowsAnyAsync<HubException>(() =>
			harness.UsingAsync(scope =>
				scope.GetRequiredService<MyloMail.Api.Transfers.CrossAccountTransferService>()
					.EnqueueAsync(harness.SourceAccountId, [message], harness.SourceAccountId, harness.SourceInboxId)));
		Assert.Empty(await harness.TransfersAsync());
	}

	[Fact]
	public async Task Mail_cannot_be_moved_into_drafts()
	{
		await using var harness = await TransferHarness.CreateAsync();
		var message = await harness.AddMessageAsync();

		await Assert.ThrowsAnyAsync<HubException>(() =>
			harness.UsingAsync(scope =>
				scope.GetRequiredService<MyloMail.Api.Transfers.CrossAccountTransferService>()
					.EnqueueAsync(harness.SourceAccountId, [message], harness.TargetAccountId, harness.TargetDraftsId)));
	}

	/// <summary>A message whose content has not been downloaded here yet is read from its own account.</summary>
	[Fact]
	public async Task A_message_not_yet_downloaded_is_read_from_its_account_and_moved()
	{
		await using var harness = await TransferHarness.CreateAsync();
		var message = await harness.AddMessageAsync("<fetch@example.test>", withRaw: false);

		await harness.EnqueueAsync(message);
		await harness.RunAsync();

		var copy = Assert.Single(harness.TargetProvider.Imported);
		Assert.Equal(TransferHarness.Raw("<fetch@example.test>"), copy.Raw);
		Assert.Equal(1, await harness.TrashMutationsAsync(message));
	}

	/// <summary>Reading the source is not irreversible: a failure leaves the transfer waiting and nothing sent.</summary>
	[Fact]
	public async Task A_source_that_cannot_be_read_yet_leaves_the_transfer_waiting()
	{
		await using var harness = await TransferHarness.CreateAsync();
		var message = await harness.AddMessageAsync("<later@example.test>", withRaw: false);
		harness.SourceProvider.FailFetchRawMessageWith(new IOException("source offline"));
		await harness.EnqueueAsync(message);

		Assert.True(await harness.RunAsync());
		Assert.Empty(harness.TargetProvider.Imported);
		Assert.Equal(CrossAccountTransferState.Pending, Assert.Single(await harness.TransfersAsync()).State);

		await harness.RunAsync();

		Assert.Single(harness.TargetProvider.Imported);
		Assert.Equal(1, await harness.TrashMutationsAsync(message));
	}

	[Fact]
	public async Task A_message_that_is_not_in_the_named_source_account_is_rejected()
	{
		await using var harness = await TransferHarness.CreateAsync();
		var foreign = Guid.NewGuid();
		await harness.UsingAsync(async scope =>
		{
			var context = scope.GetRequiredService<MyloMailDbContext>();
			context.Messages.Add(new Message { Id = foreign, AccountId = harness.TargetAccountId, Subject = "x" });
			await context.SaveChangesAsync();
		});

		await Assert.ThrowsAnyAsync<HubException>(() => harness.EnqueueAsync(foreign));
	}

	/// <summary>
	/// The server stored the copy but the answer was lost. Trying again would put a second copy in the
	/// other account, so the copy is looked for first; it is found, and nothing is imported twice.
	/// </summary>
	[Fact]
	public async Task A_lost_answer_after_the_copy_was_stored_does_not_import_it_again()
	{
		await using var harness = await TransferHarness.CreateAsync();
		var message = await harness.AddMessageAsync("<lost@example.test>");
		harness.TargetProvider.FailImportAfterStoringWith(new IOException("connection reset"));
		await harness.EnqueueAsync(message);

		await Assert.ThrowsAsync<IOException>(harness.RunAsync);
		Assert.Equal(CrossAccountTransferState.Dispatched, Assert.Single(await harness.TransfersAsync()).State);
		Assert.Equal(0, await harness.TrashMutationsAsync(message));

		await harness.RunAsync();

		Assert.Single(harness.TargetProvider.Imported);
		Assert.Equal(1, await harness.TrashMutationsAsync(message));
		Assert.Equal(CrossAccountTransferState.Completed, Assert.Single(await harness.TransfersAsync()).State);
	}

	/// <summary>The request never reached the server: the look finds nothing, so it is safe to import.</summary>
	[Fact]
	public async Task A_failure_before_anything_was_stored_is_retried_and_imports_once()
	{
		await using var harness = await TransferHarness.CreateAsync();
		var message = await harness.AddMessageAsync("<never@example.test>");
		harness.TargetProvider.FailImportWith(new IOException("connection refused"));
		await harness.EnqueueAsync(message);

		await Assert.ThrowsAsync<IOException>(harness.RunAsync);
		Assert.Empty(harness.TargetProvider.Imported);
		Assert.Equal(0, await harness.TrashMutationsAsync(message));

		harness.Clock.Advance(MyloMail.Api.Transfers.CrossAccountTransferService.UnknownOutcomeWindow + TimeSpan.FromSeconds(1));
		await harness.RunAsync();

		Assert.Single(harness.TargetProvider.Imported);
		Assert.Equal(1, await harness.TrashMutationsAsync(message));
	}

	/// <summary>
	/// Without a Message-ID the copy cannot be looked for, so an unknown outcome is not retried: the
	/// original is left where it is and the user is told.
	/// </summary>
	[Fact]
	public async Task An_unknown_outcome_for_a_message_without_a_message_id_is_not_retried()
	{
		await using var harness = await TransferHarness.CreateAsync();
		var message = await harness.AddMessageAsync(messageId: null);
		harness.TargetProvider.FailImportAfterStoringWith(new IOException("connection reset"));
		await harness.EnqueueAsync(message);
		await Assert.ThrowsAsync<IOException>(harness.RunAsync);

		await harness.RunAsync();

		Assert.Single(harness.TargetProvider.Imported);
		Assert.Equal(0, await harness.TrashMutationsAsync(message));
		Assert.Equal(CrossAccountTransferState.Failed, Assert.Single(await harness.TransfersAsync()).State);
		Assert.Single(harness.Events.TransferFailures);
	}

	[Fact]
	public async Task A_provider_that_cannot_import_fails_the_transfer_and_leaves_the_original_alone()
	{
		await using var harness = await TransferHarness.CreateAsync();
		var message = await harness.AddMessageAsync();
		harness.TargetProvider.FailImportWith(new NotSupportedException("cannot store a received message"));
		await harness.EnqueueAsync(message);

		await harness.RunAsync();

		Assert.Equal(0, await harness.TrashMutationsAsync(message));
		var transfer = Assert.Single(await harness.TransfersAsync());
		Assert.Equal(CrossAccountTransferState.Failed, transfer.State);
		Assert.Single(harness.Events.TransferFailures);
	}

	/// <summary>A message deleted meanwhile leaves only the copy, which is the end state wanted.</summary>
	[Fact]
	public async Task A_message_deleted_before_its_removal_is_queued_still_completes()
	{
		await using var harness = await TransferHarness.CreateAsync();
		var message = await harness.AddMessageAsync("<gone@example.test>");
		await harness.EnqueueAsync(message);
		harness.Faults.ArmAt(FaultPoints.TransferAfterImportedBeforeSourceRemoval);
		await Assert.ThrowsAsync<SimulatedCrashException>(harness.RunAsync);
		await harness.UsingAsync(async scope =>
		{
			var context = scope.GetRequiredService<MyloMailDbContext>();
			context.Messages.Remove(await context.Messages.SingleAsync(m => m.Id == message));
			await context.SaveChangesAsync();
		});
		await harness.RestartAsync();

		await harness.RunAsync();

		Assert.Equal(0, await harness.TrashMutationsAsync(message));
		Assert.Equal(CrossAccountTransferState.Completed, Assert.Single(await harness.TransfersAsync()).State);
	}

	/// <summary>
	/// Message-IDs are not unique. A message already in the folder with the same one must not be taken
	/// for the copy, or a transfer that never reached the server would delete its original.
	/// </summary>
	[Fact]
	public async Task A_message_already_in_the_folder_with_the_same_message_id_is_not_mistaken_for_the_copy()
	{
		await using var harness = await TransferHarness.CreateAsync();
		var message = await harness.AddMessageAsync("<same@example.test>");
		harness.TargetProvider.SeedMessage("INBOX", Guid.NewGuid(), DateTimeOffset.UnixEpoch);
		harness.TargetInbox.Messages.Values.Single().MessageIdHeader = "<same@example.test>";
		harness.TargetProvider.FailImportWith(new IOException("connection refused"));
		await harness.EnqueueAsync(message);

		await Assert.ThrowsAsync<IOException>(harness.RunAsync);
		await harness.RunAsync();

		// Seen once before and once now: nothing new arrived, so the import did not happen.
		Assert.Empty(harness.TargetProvider.Imported);
		Assert.Equal(0, await harness.TrashMutationsAsync(message));

		harness.Clock.Advance(MyloMail.Api.Transfers.CrossAccountTransferService.UnknownOutcomeWindow + TimeSpan.FromSeconds(1));
		await harness.RunAsync();

		Assert.Single(harness.TargetProvider.Imported);
		Assert.Equal(1, await harness.TrashMutationsAsync(message));
	}

	/// <summary>Two runs overlapping must not both import the same waiting transfer.</summary>
	[Fact]
	public async Task Two_runs_at_once_import_a_transfer_only_once()
	{
		await using var harness = await TransferHarness.CreateAsync();
		var message = await harness.AddMessageAsync("<race@example.test>");
		await harness.EnqueueAsync(message);

		await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => harness.RunAsync()));

		Assert.Single(harness.TargetProvider.Imported);
		Assert.Equal(1, await harness.TrashMutationsAsync(message));
	}

	/// <summary>Two requests to move the same message at once start one transfer, not two.</summary>
	[Fact]
	public async Task Two_requests_for_the_same_message_start_one_transfer()
	{
		await using var harness = await TransferHarness.CreateAsync();
		var message = await harness.AddMessageAsync("<twice@example.test>");

		var started = await Task.WhenAll(harness.EnqueueAsync(message), harness.EnqueueAsync(message));

		Assert.Equal(1, started.Sum());
		Assert.Single(await harness.TransfersAsync());
	}

	/// <summary>A target that keeps refusing the message is not asked forever.</summary>
	[Fact]
	public async Task A_message_the_target_keeps_failing_to_store_is_given_up_on()
	{
		await using var harness = await TransferHarness.CreateAsync();
		var message = await harness.AddMessageAsync("<stubborn@example.test>");
		await harness.EnqueueAsync(message);

		for (var attempt = 0; attempt < MyloMail.Api.Transfers.CrossAccountTransferService.MaximumAttempts + 2; attempt++)
		{
			harness.TargetProvider.FailImportWith(new InvalidOperationException("quota exceeded"));
			await harness.RunAsync();
			harness.Clock.Advance(MyloMail.Api.Transfers.CrossAccountTransferService.UnknownOutcomeWindow + TimeSpan.FromSeconds(1));
		}

		Assert.Equal(CrossAccountTransferState.Failed, Assert.Single(await harness.TransfersAsync()).State);
		Assert.Equal(0, await harness.TrashMutationsAsync(message));
		Assert.Single(harness.Events.TransferFailures);
	}

	/// <summary>A throttled source is the source's problem: the job must be told whose, and the transfer waits.</summary>
	[Fact]
	public async Task A_source_that_refuses_is_reported_as_the_source_and_the_transfer_keeps_waiting()
	{
		await using var harness = await TransferHarness.CreateAsync();
		var message = await harness.AddMessageAsync("<throttled@example.test>", withRaw: false);
		harness.SourceProvider.FailFetchRawMessageWith(new ProviderThrottledException(TimeSpan.FromMinutes(2), "slow down"));
		await harness.EnqueueAsync(message);

		var thrown = await Assert.ThrowsAsync<MyloMail.Api.Transfers.TransferSourceException>(harness.RunAsync);

		Assert.Equal(harness.SourceAccountId, thrown.SourceAccountId);
		Assert.IsType<ProviderThrottledException>(thrown.InnerException);
		Assert.Equal(CrossAccountTransferState.Pending, Assert.Single(await harness.TransfersAsync()).State);
		Assert.Empty(harness.TargetProvider.Imported);
	}

	/// <summary>The message must outlive the sweep that reclaims deleted ones while it is still being moved.</summary>
	[Fact]
	public async Task A_message_being_moved_is_not_collected_as_a_tombstone()
	{
		await using var harness = await TransferHarness.CreateAsync();
		var message = await harness.AddMessageAsync("<keep@example.test>", withRaw: false);
		await harness.EnqueueAsync(message);
		await harness.UsingAsync(async scope =>
		{
			var context = scope.GetRequiredService<MyloMailDbContext>();
			context.MessageMailboxes.RemoveRange(context.MessageMailboxes.Where(m => m.MessageId == message));
			await context.SaveChangesAsync();
		});

		for (var sweep = 0; sweep < 3; sweep++)
		{
			harness.Clock.Advance(TimeSpan.FromDays(2));
			await harness.UsingAsync(scope =>
				scope.GetRequiredService<MyloMail.Api.Scheduling.TombstoneGcJobs>().SweepAsync(harness.SourceAccountId));
		}

		Assert.Equal(1, await harness.UsingAsync(async scope =>
			await scope.GetRequiredService<MyloMailDbContext>().Messages.CountAsync(m => m.Id == message)));
	}

	/// <summary>
	/// Two runs that both read the transfer as waiting must still import it once. Held inside the read
	/// of the source message, which happens before the transfer is claimed, so both really are waiting.
	/// </summary>
	[Fact]
	public async Task Two_runs_that_both_saw_the_transfer_waiting_import_it_once()
	{
		await using var harness = await TransferHarness.CreateAsync();
		var message = await harness.AddMessageAsync("<held@example.test>", withRaw: false);
		await harness.EnqueueAsync(message);
		var arrived = 0;
		var bothArrived = new TaskCompletionSource();
		harness.SourceProvider.BeforeFetchRawMessageReturnAsync = async _ =>
		{
			if (Interlocked.Increment(ref arrived) == 2)
			{
				bothArrived.SetResult();
			}

			// A run that is not allowed in alongside the first never arrives, so do not wait for it for long.
			await Task.WhenAny(bothArrived.Task, Task.Delay(TimeSpan.FromSeconds(1)));
		};

		await Task.WhenAll(harness.RunAsync(), harness.RunAsync());

		Assert.Single(harness.TargetProvider.Imported);
		Assert.Equal(1, await harness.TrashMutationsAsync(message));
	}
}
