using Microsoft.Extensions.DependencyInjection;
using MyloMail.Api.Domain;
using MyloMail.Api.FaultInjection;
using Xunit;

namespace MyloMail.Api.Tests.Transfers;

/// <summary>
/// The crash windows of a move between accounts (§16). The rule under test: after any crash the
/// message exists in at least one account and is never copied twice, and the original is removed
/// exactly once and only after the copy is known to exist.
/// </summary>
[Trait("Category", "FaultInjection")]
[Trait("Category", "Deep")]
public sealed class CrossAccountTransferCrashTests
{
	/// <summary>
	/// The dispatched write exists for this window: the call has not been made, but the record says
	/// it may have been. Recovery must look, find nothing, and import — once.
	/// </summary>
	[Fact]
	public async Task A_crash_after_dispatched_before_the_import_imports_the_message_once()
	{
		await using var harness = await TransferHarness.CreateAsync();
		var message = await harness.AddMessageAsync("<a@example.test>");
		await harness.EnqueueAsync(message);
		harness.Faults.ArmAt(FaultPoints.TransferAfterDispatchedBeforeImport);

		await Assert.ThrowsAsync<SimulatedCrashException>(harness.RunAsync);
		Assert.Empty(harness.TargetProvider.Imported);
		Assert.Equal(CrossAccountTransferState.Dispatched, Assert.Single(await harness.TransfersAsync()).State);
		Assert.Equal(0, await harness.TrashMutationsAsync(message));

		await harness.RestartAsync();
		// Not found straight away proves nothing (a provider indexes a new message a moment after
		// storing it): the transfer keeps looking, and imports only once the copy has stayed absent.
		await harness.RunAsync();
		Assert.Empty(harness.TargetProvider.Imported);
		harness.Clock.Advance(MyloMail.Api.Transfers.CrossAccountTransferService.UnknownOutcomeWindow + TimeSpan.FromSeconds(1));
		await harness.RunAsync();

		Assert.Single(harness.TargetProvider.Imported);
		Assert.Equal(1, await harness.TrashMutationsAsync(message));
	}

	/// <summary>
	/// The copy exists in the other account but the record does not say so. Importing again would
	/// duplicate it; recovery must find the copy instead, and the original must still go.
	/// </summary>
	[Fact]
	public async Task A_crash_after_the_import_before_it_is_recorded_does_not_copy_the_message_twice()
	{
		await using var harness = await TransferHarness.CreateAsync();
		var message = await harness.AddMessageAsync("<b@example.test>");
		await harness.EnqueueAsync(message);
		harness.Faults.ArmAt(FaultPoints.TransferAfterImportBeforeRecorded);

		await Assert.ThrowsAsync<SimulatedCrashException>(harness.RunAsync);
		Assert.Single(harness.TargetProvider.Imported);
		Assert.Equal(0, await harness.TrashMutationsAsync(message));

		await harness.RestartAsync();
		await harness.RunAsync();

		Assert.Single(harness.TargetProvider.Imported);
		Assert.Equal(1, await harness.TrashMutationsAsync(message));
		Assert.Equal(CrossAccountTransferState.Completed, Assert.Single(await harness.TransfersAsync()).State);
	}

	/// <summary>The copy is confirmed; the original has not been touched. It must go, once.</summary>
	[Fact]
	public async Task A_crash_after_the_copy_is_recorded_before_the_original_is_removed_still_removes_it_once()
	{
		await using var harness = await TransferHarness.CreateAsync();
		var message = await harness.AddMessageAsync("<c@example.test>");
		await harness.EnqueueAsync(message);
		harness.Faults.ArmAt(FaultPoints.TransferAfterImportedBeforeSourceRemoval);

		await Assert.ThrowsAsync<SimulatedCrashException>(harness.RunAsync);
		Assert.Equal(CrossAccountTransferState.Imported, Assert.Single(await harness.TransfersAsync()).State);
		Assert.Equal(0, await harness.TrashMutationsAsync(message));

		await harness.RestartAsync();
		await harness.RunAsync();

		Assert.Single(harness.TargetProvider.Imported);
		Assert.Equal(1, await harness.TrashMutationsAsync(message));
	}

	/// <summary>
	/// The removal is queued but the transfer does not say so. Running it again must not queue a
	/// second removal.
	/// </summary>
	[Fact]
	public async Task A_crash_after_the_removal_is_queued_does_not_queue_it_twice()
	{
		await using var harness = await TransferHarness.CreateAsync();
		var message = await harness.AddMessageAsync("<d@example.test>");
		await harness.EnqueueAsync(message);
		harness.Faults.ArmAt(FaultPoints.TransferAfterSourceRemovalQueuedBeforeCompleted);

		await Assert.ThrowsAsync<SimulatedCrashException>(harness.RunAsync);
		Assert.Equal(1, await harness.TrashMutationsAsync(message));

		await harness.RestartAsync();
		await harness.RunAsync();

		Assert.Single(harness.TargetProvider.Imported);
		Assert.Equal(1, await harness.TrashMutationsAsync(message));
		Assert.Equal(CrossAccountTransferState.Completed, Assert.Single(await harness.TransfersAsync()).State);
	}

	/// <summary>
	/// Startup reconciliation must find every unfinished transfer, or a crash leaves mail in limbo
	/// with nothing to resume it.
	/// </summary>
	[Fact]
	public async Task Startup_reconciliation_finds_the_target_of_every_unfinished_transfer()
	{
		await using var harness = await TransferHarness.CreateAsync();
		var message = await harness.AddMessageAsync("<e@example.test>");
		await harness.EnqueueAsync(message);
		harness.Faults.ArmAt(FaultPoints.TransferAfterImportBeforeRecorded);
		await Assert.ThrowsAsync<SimulatedCrashException>(harness.RunAsync);
		await harness.RestartAsync();

		var work = await harness.UsingAsync(scope =>
			scope.GetRequiredService<MyloMail.Api.Mutations.StartupReconciliation>().FindAsync());

		Assert.Equal([harness.TargetAccountId], work.TransferAccounts);
	}
}
