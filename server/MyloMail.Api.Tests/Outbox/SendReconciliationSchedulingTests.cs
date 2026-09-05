using Microsoft.Extensions.DependencyInjection;
using MyloMail.Api.Outbox;
using MyloMail.Api.Scheduling;
using MyloMail.Api.Tests.Fakes;
using MyloMail.Api.Tests.Mutations;
using Xunit;

namespace MyloMail.Api.Tests.Outbox;

/// <summary>
/// Two-hundred-and-ninth architecture-review pass: <see cref="SendReconciler"/>'s own doc
/// comment describes an active, time-bound decision — "how long to keep looking before giving
/// up and asking the user" — but nothing ever re-invoked it on its own schedule.
/// <see cref="OutboxJobs.RunAsync"/> is enqueued only when a new item becomes due or at
/// startup, so an ambiguous send with no other outbox activity afterward would sit forever,
/// never receiving the "check your Sent mail" message the design promises within
/// <see cref="SendReconciler.ReconciliationWindow"/>.
/// </summary>
public sealed class SendReconciliationSchedulingTests
{
	[Fact]
	public async Task A_run_leaving_an_item_ambiguous_schedules_its_own_reconciliation_check()
	{
		await using var harness = await MutationHarness.CreateAsync();
		await OutboxTests.SetUndoDelayAsync(harness, 0);
		await OutboxTests.QueueAsync(harness);
		harness.Provider.FailSendWith(new InvalidOperationException("the connection dropped"));

		await harness.UsingAsync(services => services.GetRequiredService<OutboxJobs>().RunAsync(harness.AccountId));

		var created = await harness.UsingAsync(services =>
			Task.FromResult(((RecordingJobClient)services.GetRequiredService<Hangfire.IBackgroundJobClient>()).Created)
		);
		Assert.Contains(created, job => job.Method.Name == nameof(OutboxJobs.RunAsync));
	}

	[Fact]
	public async Task A_run_with_nothing_ambiguous_schedules_no_reconciliation_check()
	{
		await using var harness = await MutationHarness.CreateAsync();
		await OutboxTests.SetUndoDelayAsync(harness, 0);
		await OutboxTests.QueueAsync(harness);

		await harness.UsingAsync(services => services.GetRequiredService<OutboxJobs>().RunAsync(harness.AccountId));

		var created = await harness.UsingAsync(services =>
			Task.FromResult(((RecordingJobClient)services.GetRequiredService<Hangfire.IBackgroundJobClient>()).Created)
		);
		Assert.DoesNotContain(created, job => job.Method.Name == nameof(OutboxJobs.RunAsync));
	}
}
