using System.Collections.Concurrent;
using Google.Apis.Requests;

namespace MyloMail.Api.Providers.Gmail;

/// <summary>
/// A per-account token bucket over Gmail's "quota units per minute per user" (§15).
/// </summary>
/// <remarks>
/// <para>
/// Gmail allows 15,000 units/min/user and answers an overdraft with a 403
/// <c>rateLimitExceeded</c>, which holds the whole account for a minute. Reacting to that is
/// the backstop; this is the thing that keeps it from happening. Backfill, the change
/// stream, content downloads and mutations of one account all draw on the same bucket, since
/// Google counts them as one user.
/// </para>
/// <para>
/// <b>Refill is 6,000 units/min, burst 1,000.</b> It was 10,000/2,500, and a first sync of a large
/// account still drew a 403 "Units per minute per user" about once a minute: every rejection costs
/// a full minute of waiting, so a lower steady rate finishes sooner than a higher one that keeps
/// overdrawing, and it leaves room for the user's other clients of the same account. The refill leaves more than half of Google's
/// allowance as headroom for requests this process cannot see (another client of the same
/// account). The burst is deliberately small: a bucket may hand out its burst plus a
/// minute's refill inside one minute, and 1,000 + 6,000 is well under 15,000, whereas a
/// burst equal to the refill would not be.
/// </para>
/// <para>
/// Waiting is reservation-based: a request deducts its units immediately, going into debt if
/// it must, and sleeps for exactly the time the debt takes to refill. Waiters therefore queue
/// in arrival order without any of them re-checking, and a cancelled wait gives its units
/// back instead of leaking budget.
/// </para>
/// <para>
/// In memory by design, like <see cref="Scheduling.AccountGate"/>: a restart forgets the
/// debt, which at worst repeats a minute of pacing the reactive path already handles. It is
/// shared per process (<see cref="Shared"/>) because the provider factory builds a new
/// provider for every call, so nothing else could carry it from one call to the next.
/// </para>
/// </remarks>
public sealed class GmailRequestBudget
{
	public const int DefaultUnitsPerMinute = 6_000;
	public const int DefaultBurstUnits = 1_000;

	/// <summary>The process-wide budget every <see cref="GmailMailProvider"/> uses unless given another.</summary>
	public static GmailRequestBudget Shared { get; } = new(TimeProvider.System);

	private readonly TimeProvider clock;
	private readonly double unitsPerSecond;
	private readonly double burstUnits;
	private readonly ConcurrentDictionary<Guid, Bucket> buckets = new();

	public GmailRequestBudget(
		TimeProvider clock,
		int unitsPerMinute = DefaultUnitsPerMinute,
		int burstUnits = DefaultBurstUnits
	)
	{
		ArgumentNullException.ThrowIfNull(clock);
		ArgumentOutOfRangeException.ThrowIfNegativeOrZero(unitsPerMinute);
		ArgumentOutOfRangeException.ThrowIfNegativeOrZero(burstUnits);
		this.clock = clock;
		unitsPerSecond = unitsPerMinute / 60d;
		this.burstUnits = burstUnits;
	}

	/// <summary>
	/// Spends <paramref name="units"/> from the account's bucket, waiting until the bucket can
	/// afford them.
	/// </summary>
	/// <exception cref="OperationCanceledException">
	/// <paramref name="ct"/> fired first. The reservation is returned.
	/// </exception>
	public async Task AcquireAsync(Guid accountId, int units, CancellationToken ct)
	{
		ArgumentOutOfRangeException.ThrowIfNegativeOrZero(units);
		ct.ThrowIfCancellationRequested();

		var bucket = buckets.GetOrAdd(accountId, _ => new Bucket(burstUnits, clock.GetTimestamp()));
		TimeSpan wait;
		lock (bucket)
		{
			Refill(bucket);
			bucket.Spent.Enqueue((clock.GetTimestamp(), units));
			bucket.Available -= units;
			wait = bucket.Available >= 0
				? TimeSpan.Zero
				: TimeSpan.FromSeconds(-bucket.Available / (unitsPerSecond * bucket.Scale));
		}

		if (wait <= TimeSpan.Zero)
		{
			return;
		}

		try
		{
			await Task.Delay(wait, clock, ct).ConfigureAwait(false);
		}
		catch (OperationCanceledException)
		{
			lock (bucket)
			{
				Refill(bucket);
				bucket.Available = Math.Min(burstUnits, bucket.Available + units);
			}

			throw;
		}
	}

	private void Refill(Bucket bucket)
	{
		var now = clock.GetTimestamp();
		var elapsed = clock.GetElapsedTime(bucket.Timestamp, now);
		bucket.Timestamp = now;
		// A rate cut after a refusal heals by a tenth per minute, but only up to a ceiling just under
		// the rate it was refused at; that ceiling itself rises by two percent a minute. The rate
		// therefore settles near what the provider enforces and probes upward slowly, instead of
		// climbing straight back to the rate that was refused and paying a minute's lockout for it
		// every few minutes. A passing condition still heals: the ceiling reaches the configured
		// rate in the end.
		bucket.Ceiling = Math.Min(1, bucket.Ceiling * Math.Pow(CeilingGrowthPerMinute, elapsed.TotalMinutes));
		bucket.Scale = Math.Min(bucket.Ceiling, bucket.Scale * Math.Pow(1.1, elapsed.TotalMinutes));
		bucket.Available = Math.Min(
			burstUnits * bucket.Scale,
			bucket.Available + elapsed.TotalSeconds * unitsPerSecond * bucket.Scale
		);
	}

	/// <summary>
	/// The provider refused a request. If this app had charged far less than the configured rate
	/// in the last minute, the limit actually enforced is lower than assumed, and the account is
	/// slowed to about half of what it had been spending when it was refused.
	/// </summary>
	/// <remarks>
	/// Measured on a real account: refused at 540 units in a minute against an assumed allowance
	/// of 15,000. Pacing at a fixed rate the provider does not honour just trades each page for a
	/// minute of lockout, so the rate is found by being refused, not configured.
	/// </remarks>
	public void NoteRefused(Guid accountId)
	{
		if (!buckets.TryGetValue(accountId, out var bucket))
		{
			return;
		}

		var spent = SpentInLastMinute(accountId);
		lock (bucket)
		{
			Refill(bucket);
			var target = Math.Max(MinimumScale, spent / 2.0 / (unitsPerSecond * 60));
			bucket.Scale = Math.Min(bucket.Scale, target);
			// Never again to be spent at the rate it was refused at: the next probe stays below it.
			bucket.Ceiling = Math.Min(bucket.Ceiling, Math.Max(MinimumScale, CeilingFraction * spent / (unitsPerSecond * 60)));
			// Nothing in hand: the next request waits at the slower rate from a standing start.
			bucket.Available = Math.Min(bucket.Available, 0);
		}
	}

	private const double MinimumScale = 0.02;
	private const double CeilingFraction = 0.8;
	private const double CeilingGrowthPerMinute = 1.02;

	/// <summary>The rate the account is being paced at now, in units a minute.</summary>
	internal double CurrentUnitsPerMinute(Guid accountId)
	{
		if (!buckets.TryGetValue(accountId, out var bucket))
		{
			return unitsPerSecond * 60;
		}

		lock (bucket)
		{
			Refill(bucket);
			return unitsPerSecond * 60 * bucket.Scale;
		}
	}

	/// <summary>
	/// Units this process has charged to the account in the last minute — what the bucket believes
	/// it has spent, for comparing with what Google says when it refuses a request.
	/// </summary>
	public int SpentInLastMinute(Guid accountId)
	{
		if (!buckets.TryGetValue(accountId, out var bucket))
		{
			return 0;
		}

		lock (bucket)
		{
			var now = clock.GetTimestamp();
			while (bucket.Spent.Count > 0 && clock.GetElapsedTime(bucket.Spent.Peek().Timestamp, now) > TimeSpan.FromMinutes(1))
			{
				bucket.Spent.Dequeue();
			}

			return bucket.Spent.Sum(entry => entry.Units);
		}
	}

	private sealed class Bucket(double available, long timestamp)
	{
		public readonly Queue<(long Timestamp, int Units)> Spent = new();
		public double Scale = 1;
		public double Ceiling = 1;
		public double Available = available;
		public long Timestamp = timestamp;
	}
}

/// <summary>Which account's bucket a <c>GmailService</c>'s requests draw from.</summary>
internal sealed record GmailQuotaMeter(Guid AccountId, GmailRequestBudget Budget);

/// <summary>
/// What each Gmail API method costs in quota units, from Google's published table
/// (developers.google.com/workspace/gmail/api/reference/quota).
/// </summary>
/// <remarks>
/// Keyed by the SDK's request type — <c>MessagesResource.GetRequest</c> — rather than by
/// path, so a renamed route cannot silently turn a 100-unit send into the default. A type
/// absent from the table is charged <see cref="Unlisted"/> instead of nothing: an unmetered
/// call is the failure this exists to prevent.
/// </remarks>
internal static class GmailQuotaUnits
{
	/// <summary>Charged for a request type the table does not name.</summary>
	internal const int Unlisted = 5;

	private static readonly Dictionary<string, int> Costs = new(StringComparer.Ordinal)
	{
		["UsersResource.GetProfileRequest"] = 1,
		["UsersResource.StopRequest"] = 50,
		["UsersResource.WatchRequest"] = 100,
		["HistoryResource.ListRequest"] = 2,
		["LabelsResource.CreateRequest"] = 5,
		["LabelsResource.DeleteRequest"] = 5,
		["LabelsResource.GetRequest"] = 1,
		["LabelsResource.ListRequest"] = 1,
		["LabelsResource.PatchRequest"] = 5,
		["LabelsResource.UpdateRequest"] = 5,
		["MessagesResource.BatchDeleteRequest"] = 50,
		["MessagesResource.BatchModifyRequest"] = 50,
		["MessagesResource.DeleteRequest"] = 10,
		["MessagesResource.GetRequest"] = 5,
		["MessagesResource.ImportRequest"] = 25,
		["MessagesResource.InsertRequest"] = 25,
		["MessagesResource.ListRequest"] = 5,
		["MessagesResource.ModifyRequest"] = 5,
		["MessagesResource.SendRequest"] = 100,
		["MessagesResource.TrashRequest"] = 5,
		["MessagesResource.UntrashRequest"] = 5,
		["AttachmentsResource.GetRequest"] = 5,
		["DraftsResource.CreateRequest"] = 10,
		["DraftsResource.DeleteRequest"] = 10,
		["DraftsResource.GetRequest"] = 5,
		["DraftsResource.ListRequest"] = 5,
		["DraftsResource.SendRequest"] = 100,
		["DraftsResource.UpdateRequest"] = 15,
		["ThreadsResource.DeleteRequest"] = 20,
		["ThreadsResource.GetRequest"] = 10,
		["ThreadsResource.ListRequest"] = 10,
		["ThreadsResource.ModifyRequest"] = 10,
		["ThreadsResource.TrashRequest"] = 10,
		["ThreadsResource.UntrashRequest"] = 10,
	};

	public static int For(IClientServiceRequest request)
	{
		var type = request.GetType();
		return Costs.TryGetValue($"{type.DeclaringType?.Name}.{type.Name}", out var units)
			? units
			: Unlisted;
	}
}
