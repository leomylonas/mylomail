using System.Net.Sockets;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MyloMail.Api.Scheduling;
using MyloMail.Api.Tests.Fakes;
using MyloMail.Api.Tests.Sync;
using Xunit;

namespace MyloMail.Api.Tests.Scheduling;

/// <summary>
/// §1091 requires a network-class failure with no explicit provider signal (unlike
/// <c>ProviderThrottledException</c>'s own exact delay) to "fall back to the existing
/// exponential backoff" — but <see cref="SyncJobs"/>'s five self-rescheduling poll loops all
/// rescheduled a network-class failure at the exact same fixed 30-second delay every time,
/// regardless of how many times in a row that account had already failed. A genuinely offline
/// machine would poll a dead socket forever at the same rate rather than backing off, and the
/// doc's own claim about this behaviour was simply false.
/// </summary>
public sealed class NetworkRetryBackoffTests
{
	[Fact]
	public void The_delay_doubles_each_consecutive_failure_and_caps_at_thirty_minutes()
	{
		var accountId = Guid.NewGuid();

		var delays = Enumerable.Range(0, 10).Select(_ => SyncJobs.NextNetworkRetryDelay(accountId)).ToArray();

		Assert.Equal(TimeSpan.FromSeconds(30), delays[0]);
		Assert.Equal(TimeSpan.FromSeconds(60), delays[1]);
		Assert.Equal(TimeSpan.FromSeconds(120), delays[2]);
		Assert.Equal(TimeSpan.FromSeconds(240), delays[3]);
		// 30s * 2^10 would be ~8.5 hours; the cap must have kicked in well before this.
		Assert.Equal(TimeSpan.FromMinutes(30), delays[^1]);
		Assert.True(delays.SequenceEqual(delays.OrderBy(d => d)), "delay must never decrease while failures keep occurring");
	}

	/// <summary>
	/// Exercises the real production path, not just the pure helper above: two consecutive
	/// network-class failures on <see cref="SyncJobs.TopologyAsync"/> for the same account must
	/// leave that account's streak advanced (proving the catch site actually calls the shared
	/// helper), and one subsequent success must reset it back to the base delay (proving
	/// <c>GuardAsync</c>'s reset actually runs) — reverting either change independently makes
	/// one of the two assertions below fail.
	/// </summary>
	[Fact]
	public async Task A_topology_success_resets_the_streak_a_prior_failure_run_built_up()
	{
		await using var harness = await SyncHarness.CreateAsync(ProviderShapes.Gmail);

		harness.Provider.FailListMailboxesWith(new SocketException());
		await harness.UsingAsync(async scope =>
			await scope.GetRequiredService<SyncJobs>().TopologyAsync(harness.Account.Id)
		);
		harness.Provider.FailListMailboxesWith(new SocketException());
		await harness.UsingAsync(async scope =>
			await scope.GetRequiredService<SyncJobs>().TopologyAsync(harness.Account.Id)
		);

		// Two real failures already happened for this account, so its streak sits at 2 — the
		// next call the pure helper would make is the *third*, i.e. 30s * 2^2 = 120s.
		Assert.Equal(TimeSpan.FromSeconds(120), SyncJobs.NextNetworkRetryDelay(harness.Account.Id));
		// That call above also advanced the streak to 3; undo the probe itself before the
		// success run below so the test's own assertion doesn't taint what it's checking.
		var afterProbe = SyncJobs.NextNetworkRetryDelay(harness.Account.Id);
		Assert.Equal(TimeSpan.FromSeconds(240), afterProbe);

		// No FailListMailboxesWith armed this time — a genuine success.
		await harness.UsingAsync(async scope =>
			await scope.GetRequiredService<SyncJobs>().TopologyAsync(harness.Account.Id)
		);

		Assert.Equal(TimeSpan.FromSeconds(30), SyncJobs.NextNetworkRetryDelay(harness.Account.Id));
	}
}
