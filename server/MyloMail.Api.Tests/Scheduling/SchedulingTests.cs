using System.Reflection;
using Hangfire;
using Microsoft.Extensions.Time.Testing;
using MyloMail.Api.Scheduling;
using Xunit;

namespace MyloMail.Api.Tests.Scheduling;

/// <summary>
/// The scheduling rules from §3 that exist because the default behaviour is wrong here.
/// </summary>
public sealed class SchedulingTests
{
	/// <summary>
	/// Retry is decided in our code, never by Hangfire. An auth failure must not retry at
	/// all and a throttled call must wait exactly the delay the provider named; Hangfire's
	/// own curve would fire underneath both, retrying what must not be retried and
	/// double-scheduling what was already rescheduled.
	/// </summary>
	[Theory]
	[InlineData(typeof(SyncJobs))]
	[InlineData(typeof(MutationJobs))]
	[InlineData(typeof(TombstoneGcJobs))]
	public void Every_job_type_disables_automatic_retry(Type jobType)
	{
		var attribute = jobType.GetCustomAttribute<AutomaticRetryAttribute>();

		Assert.NotNull(attribute);
		Assert.Equal(0, attribute.Attempts);
	}

	/// <summary>
	/// One throttling response holds the whole account. Otherwise thirty mailbox jobs wake
	/// together, are all throttled, and the account makes no progress while looking busy.
	/// </summary>
	[Fact]
	public void Throttling_one_call_holds_back_the_whole_account()
	{
		var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
		var gate = new AccountGate(clock);
		var account = Guid.NewGuid();
		var other = Guid.NewGuid();

		gate.Throttle(account, TimeSpan.FromSeconds(30));

		Assert.Equal(TimeSpan.FromSeconds(30), gate.Delay(account));
		Assert.Equal(TimeSpan.Zero, gate.Delay(other));
	}

	/// <summary>The delay is exactly what the provider asked for, and expires exactly then.</summary>
	[Fact]
	public void The_gate_opens_when_the_named_delay_has_passed()
	{
		var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
		var gate = new AccountGate(clock);
		var account = Guid.NewGuid();

		gate.Throttle(account, TimeSpan.FromSeconds(30));
		clock.Advance(TimeSpan.FromSeconds(29));
		Assert.Equal(TimeSpan.FromSeconds(1), gate.Delay(account));

		// Past the expiry, not merely to it: at the exact instant the remaining time is
		// already zero, so a delay returned unclamped would be indistinguishable from a
		// clamped one and this would assert nothing.
		clock.Advance(TimeSpan.FromSeconds(5));
		Assert.Equal(TimeSpan.Zero, gate.Delay(account));
	}

	/// <summary>
	/// A second, shorter signal must not shorten a pause an earlier response already asked
	/// for — that would walk straight back into the throttle the longer one was avoiding.
	/// </summary>
	[Fact]
	public void A_shorter_signal_never_shortens_an_existing_pause()
	{
		var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
		var gate = new AccountGate(clock);
		var account = Guid.NewGuid();

		gate.Throttle(account, TimeSpan.FromMinutes(5));
		gate.Throttle(account, TimeSpan.FromSeconds(10));

		Assert.Equal(TimeSpan.FromMinutes(5), gate.Delay(account));
	}

	/// <summary>
	/// A self-scheduling loop enqueues its own successor, so starting one twice doubles the
	/// poll rate — and topology reconciliation, which starts them, runs repeatedly.
	/// </summary>
	[Fact]
	public void A_poll_loop_can_only_be_started_once_per_scope()
	{
		var registry = new PollRegistry();
		var account = Guid.NewGuid();
		var mailbox = Guid.NewGuid();

		Assert.True(registry.TryStart(account, mailbox));
		Assert.False(registry.TryStart(account, mailbox));

		// A different scope under the same account is a different stream.
		Assert.True(registry.TryStart(account, Guid.NewGuid()));
	}

	/// <summary>A stopped loop can be started again — after a pause, or after a restart.</summary>
	[Fact]
	public void A_stopped_poll_loop_can_be_restarted()
	{
		var registry = new PollRegistry();
		var account = Guid.NewGuid();
		var mailbox = Guid.NewGuid();

		Assert.True(registry.TryStart(account, mailbox));
		registry.Stop(account, mailbox);

		Assert.True(registry.TryStart(account, mailbox));
	}

	/// <summary>Pausing an account releases every one of its loops, so resume can restart them.</summary>
	[Fact]
	public void Stopping_an_account_releases_all_of_its_loops()
	{
		var registry = new PollRegistry();
		var account = Guid.NewGuid();
		var first = Guid.NewGuid();
		var second = Guid.NewGuid();

		registry.TryStart(account, first);
		registry.TryStart(account, second);
		registry.StopAll(account);

		Assert.True(registry.TryStart(account, first));
		Assert.True(registry.TryStart(account, second));
	}

	[Fact]
	public void An_unthrottled_account_waits_for_nothing()
	{
		var gate = new AccountGate(new FakeTimeProvider(DateTimeOffset.UnixEpoch));

		Assert.Equal(TimeSpan.Zero, gate.Delay(Guid.NewGuid()));
	}
}
