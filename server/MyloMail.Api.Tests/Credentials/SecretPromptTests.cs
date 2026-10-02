using MyloMail.Api.Credentials;
using Xunit;

namespace MyloMail.Api.Tests.Credentials;

public sealed class SecretPromptTests
{
	private sealed class Subscription : IDisposable
	{
		public bool Disposed;
		public void Dispose() => Disposed = true;
	}

	[Fact]
	public async Task A_prompt_that_completes_inside_start_is_not_missed()
	{
		var subscription = new Subscription();
		Action<bool>? handler = null;
		var completed = await SecretPrompt.RunAsync(
			// The handler is registered before start runs, so the immediate signal lands.
			async onCompleted => { await Task.Yield(); handler = onCompleted; return subscription; },
			() => { handler!(false); return Task.CompletedTask; },
			TimeSpan.FromSeconds(5),
			CancellationToken.None
		);

		Assert.True(completed);
		Assert.True(subscription.Disposed);
	}

	[Fact]
	public async Task A_dismissed_prompt_reports_false()
	{
		Action<bool>? handler = null;
		var completed = await SecretPrompt.RunAsync(
			onCompleted => { handler = onCompleted; return Task.FromResult<IDisposable>(new Subscription()); },
			() => { handler!(true); return Task.CompletedTask; },
			TimeSpan.FromSeconds(5),
			CancellationToken.None
		);

		Assert.False(completed);
	}

	[Fact]
	public async Task An_unanswered_prompt_times_out_and_still_unsubscribes()
	{
		var subscription = new Subscription();
		await Assert.ThrowsAsync<TimeoutException>(() => SecretPrompt.RunAsync(
			_ => Task.FromResult<IDisposable>(subscription),
			() => Task.CompletedTask,
			TimeSpan.FromMilliseconds(50),
			CancellationToken.None
		));

		Assert.True(subscription.Disposed);
	}

	[Fact]
	public async Task Caller_cancellation_is_not_reported_as_a_timeout()
	{
		using var cts = new CancellationTokenSource();
		var run = SecretPrompt.RunAsync(
			_ => Task.FromResult<IDisposable>(new Subscription()),
			() => Task.CompletedTask,
			TimeSpan.FromMinutes(1),
			cts.Token
		);
		cts.Cancel();

		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
	}

	[Fact]
	public void The_secret_service_client_has_no_way_to_lock_the_keyring()
	{
		var members = typeof(LinuxSecretServiceCredentialStore).Assembly.GetTypes()
			.Where(type => type.Namespace == "MyloMail.Api.Credentials.SecretService" || type == typeof(LinuxSecretServiceCredentialStore))
			.SelectMany(type => type.GetMembers(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.DeclaredOnly))
			.Select(member => member.Name)
			.Where(name => name.StartsWith("Lock", StringComparison.Ordinal));

		Assert.Empty(members);
	}
}

public sealed class SecretUnlockGateTests
{
	private sealed class Clock : TimeProvider
	{
		public DateTimeOffset Now = DateTimeOffset.UnixEpoch;
		public override DateTimeOffset GetUtcNow() => Now;
	}

	[Fact]
	public async Task Concurrent_callers_never_prompt_at_the_same_time()
	{
		var gate = new SecretUnlockGate(TimeSpan.FromMinutes(5), TimeProvider.System);
		var active = 0;
		var overlapped = false;
		async Task<bool> Unlock(CancellationToken _)
		{
			if (Interlocked.Increment(ref active) > 1) overlapped = true;
			await Task.Delay(30);
			Interlocked.Decrement(ref active);
			return true;
		}

		await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => gate.RunAsync(Unlock, false, CancellationToken.None)));

		Assert.False(overlapped);
	}

	[Fact]
	public async Task Background_callers_stand_down_after_a_declined_prompt_but_the_user_can_retry()
	{
		var clock = new Clock();
		var gate = new SecretUnlockGate(TimeSpan.FromMinutes(5), clock);
		var attempts = 0;
		Task<bool> Decline(CancellationToken _) { attempts++; return Task.FromResult(false); }
		Task<bool> Accept(CancellationToken _) { attempts++; return Task.FromResult(true); }

		Assert.False(await gate.RunAsync(Decline, false, CancellationToken.None));
		Assert.False(await gate.RunAsync(Accept, false, CancellationToken.None));
		Assert.Equal(1, attempts);

		Assert.True(await gate.RunAsync(Accept, true, CancellationToken.None));
		Assert.Equal(2, attempts);
	}

	[Fact]
	public async Task Background_callers_may_prompt_again_once_the_cooldown_has_passed()
	{
		var clock = new Clock();
		var gate = new SecretUnlockGate(TimeSpan.FromMinutes(5), clock);
		await gate.RunAsync(_ => Task.FromResult(false), false, CancellationToken.None);
		clock.Now += TimeSpan.FromMinutes(6);

		Assert.True(await gate.RunAsync(_ => Task.FromResult(true), false, CancellationToken.None));
	}

	[Fact]
	public async Task A_failed_attempt_starts_the_cooldown_and_rethrows()
	{
		var gate = new SecretUnlockGate(TimeSpan.FromMinutes(5), new Clock());
		await Assert.ThrowsAsync<InvalidOperationException>(() =>
			gate.RunAsync(_ => throw new InvalidOperationException("no prompter"), false, CancellationToken.None));

		Assert.False(await gate.RunAsync(_ => Task.FromResult(true), false, CancellationToken.None));
	}
}
