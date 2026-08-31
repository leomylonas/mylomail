using Microsoft.Extensions.DependencyInjection;
using MyloMail.Api.Domain;
using MyloMail.Api.Outbox;
using MyloMail.Api.Tests.Mutations;
using Xunit;

namespace MyloMail.Api.Tests.Outbox;

/// <summary>
/// Queueing a send asks for it to be dispatched (§15).
/// </summary>
/// <remarks>
/// Without this the only thing that runs the outbox is the startup sweep, so a message would
/// sit queued and durable and never leave — which is what happened, and was caught by asking
/// the SMTP server rather than the app.
/// </remarks>
public sealed class OutboxDispatchTests
{
	[Fact]
	public async Task Queueing_a_send_requests_dispatch_for_the_account()
	{
		await using var harness = await MutationHarness.CreateAsync();

		var item = await OutboxTests.QueueAsync(harness);

		var request = Assert.Single(harness.OutboxDispatcher.Requests);
		Assert.Equal(item.AccountId, request.AccountId);
	}

	/// <summary>
	/// Dispatch waits for the undo window rather than firing immediately.
	/// </summary>
	/// <remarks>
	/// Sending at once would deliver the message while the user still believes they can stop
	/// it — the window is the entire feature, not a delay to be optimised away.
	/// </remarks>
	[Fact]
	public async Task Dispatch_is_delayed_until_the_undo_window_closes()
	{
		await using var harness = await MutationHarness.CreateAsync();
		await OutboxTests.SetUndoDelayAsync(harness, 30);

		await OutboxTests.QueueAsync(harness);

		Assert.Equal(TimeSpan.FromSeconds(30), Assert.Single(harness.OutboxDispatcher.Requests).Delay);
	}

	[Fact]
	public async Task A_zero_window_dispatches_at_once()
	{
		await using var harness = await MutationHarness.CreateAsync();
		await OutboxTests.SetUndoDelayAsync(harness, 0);

		await OutboxTests.QueueAsync(harness);

		Assert.Equal(TimeSpan.Zero, Assert.Single(harness.OutboxDispatcher.Requests).Delay);
	}
}
