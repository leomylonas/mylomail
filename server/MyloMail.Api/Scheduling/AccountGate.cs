namespace MyloMail.Api.Scheduling;

/// <summary>
/// The account-level throttle gate (§3). One provider throttling response holds back every
/// job for that account, not just the one that hit it.
/// </summary>
/// <remarks>
/// <para>
/// Without this, thirty mailbox jobs under one account all wake at the same moment, all get
/// throttled, and the account makes no progress while looking busy. The gate is what turns
/// one <c>Retry-After</c> into a coordinated pause.
/// </para>
/// <para>
/// It is in-memory on purpose. A throttle window is short-lived advice about the near
/// future, not a fact about the account, and persisting it would create a second source of
/// truth that could outlive its own relevance across a restart — the provider will simply
/// say so again if it still applies.
/// </para>
/// </remarks>
public sealed class AccountGate(TimeProvider clock)
{
	private readonly Dictionary<Guid, DateTimeOffset> allowedFrom = [];
	private readonly object guard = new();

	/// <summary>Holds every job for this account until <paramref name="retryAfter"/> has passed.</summary>
	public void Throttle(Guid accountId, TimeSpan retryAfter)
	{
		var until = clock.GetUtcNow() + retryAfter;
		lock (guard)
		{
			// The longest outstanding window wins: a second, shorter signal must not shorten
			// a pause another response already asked for.
			allowedFrom[accountId] =
				allowedFrom.TryGetValue(accountId, out var existing) && existing > until ? existing : until;
		}
	}

	/// <summary>How long a job for this account must wait, or zero if it may proceed.</summary>
	public TimeSpan Delay(Guid accountId)
	{
		lock (guard)
		{
			if (!allowedFrom.TryGetValue(accountId, out var until))
			{
				return TimeSpan.Zero;
			}

			var remaining = until - clock.GetUtcNow();
			return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
		}
	}

	public void Clear(Guid accountId)
	{
		lock (guard)
		{
			allowedFrom.Remove(accountId);
		}
	}
}
