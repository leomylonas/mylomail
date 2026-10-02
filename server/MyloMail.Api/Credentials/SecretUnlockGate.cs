namespace MyloMail.Api.Credentials;

/// <summary>
/// Serialises unlock prompts process-wide. Sync, outbox and content jobs for every account can
/// all find the keyring locked at once; without this each would raise its own prompt. After a
/// prompt is declined or fails, background callers stand down for a while rather than
/// re-prompting on every poll; a user-initiated unlock always goes through.
/// </summary>
internal sealed class SecretUnlockGate(TimeSpan cooldown, TimeProvider time)
{
	public static readonly SecretUnlockGate Shared = new(TimeSpan.FromMinutes(5), TimeProvider.System);

	private readonly SemaphoreSlim turn = new(1, 1);
	private DateTimeOffset? failedAt;

	/// <returns>True when <paramref name="unlock"/> succeeded; false when it was declined or skipped during cooldown.</returns>
	public async Task<bool> RunAsync(Func<CancellationToken, Task<bool>> unlock, bool userInitiated, CancellationToken ct)
	{
		await turn.WaitAsync(ct);
		try
		{
			if (!userInitiated && failedAt is { } at && time.GetUtcNow() - at < cooldown) return false;

			bool succeeded;
			try
			{
				succeeded = await unlock(ct);
			}
			catch (Exception ex) when (ex is not OperationCanceledException)
			{
				failedAt = time.GetUtcNow();
				throw;
			}
			failedAt = succeeded ? null : time.GetUtcNow();
			return succeeded;
		}
		finally
		{
			turn.Release();
		}
	}
}
