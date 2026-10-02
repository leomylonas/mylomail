namespace MyloMail.Api.Credentials;

/// <summary>Runs one Secret Service prompt to completion: subscribe, start, wait for `Completed`.</summary>
internal static class SecretPrompt
{
	/// <summary>Long enough for a person to type a password, short enough that a prompt nobody can see does not hold a job forever.</summary>
	public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(2);

	/// <returns>True when the prompt completed, false when the user dismissed it.</returns>
	/// <exception cref="TimeoutException">No `Completed` signal arrived within <paramref name="timeout"/>.</exception>
	public static async Task<bool> RunAsync(
		Func<Action<bool>, Task<IDisposable>> subscribe,
		Func<Task> start,
		TimeSpan timeout,
		CancellationToken ct
	)
	{
		var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
		// Subscribe before starting: a prompt that completes immediately must not be missed.
		using var subscription = await subscribe(dismissed => completion.TrySetResult(!dismissed));
		await start();

		using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
		deadline.CancelAfter(timeout);
		using var registration = deadline.Token.Register(() => completion.TrySetCanceled(deadline.Token));
		try
		{
			return await completion.Task;
		}
		catch (OperationCanceledException) when (!ct.IsCancellationRequested)
		{
			throw new TimeoutException("The Secret Service prompt was not answered in time.");
		}
	}
}
