using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using MyloMail.Api.Domain;
using MyloMail.Api.Persistence;

namespace MyloMail.Api.Mutations;

/// <summary>
/// Claims eligible mutations as an atomic leased transition (§6).
/// </summary>
public sealed class MutationClaimService(MyloMailDbContext context, TimeProvider clock)
{
	/// <summary>
	/// The claim itself: one conditional <c>UPDATE</c> whose <c>WHERE</c> re-checks both lease
	/// state and chain position, so the decision to take an item and the taking of it are the
	/// same statement.
	/// </summary>
	/// <remarks>
	/// This is deliberately SQL rather than LINQ. The predicate is the atomicity guarantee,
	/// and it must be expressible exactly — not left to whether a given EF version can
	/// translate a correlated aggregate, which it silently could not.
	/// </remarks>
	/// <remarks>
	/// The lease predicate is a compare-and-swap on the state and owner that were just read,
	/// not a timestamp comparison. Whether a lease has expired is decided in C# from the
	/// value read; the swap then fails if anything about the item's ownership changed in
	/// between. Comparing the expiry in SQL would rest on EF's textual
	/// <c>DateTimeOffset</c> encoding sorting lexicographically, which it does not reliably
	/// do — its fractional seconds are variable length.
	/// </remarks>
	private const string ClaimSql = """
		UPDATE "MutationItems"
		SET "State" = $leased, "LeaseOwner" = $owner, "LeaseExpiresAt" = $expiry
		WHERE "Id" = $id
		  AND "State" = $expectedState
		  AND "LeaseOwner" IS $expectedOwner
		  AND "Sequence" = (
			SELECT MIN(head."Sequence")
			FROM "MutationItems" AS head
			WHERE head."AccountId" = "MutationItems"."AccountId"
			  AND head."MessageId" = "MutationItems"."MessageId"
			  AND head."State" NOT IN ($completed, $failed, $cancelled)
		  );
		""";

	/// <summary>
	/// Claims up to <paramref name="max"/> eligible heads for one account.
	/// </summary>
	/// <remarks>
	/// <para>
	/// Only the <b>lowest non-terminal sequence</b> for a message is eligible. Different
	/// messages proceed concurrently, and a chain is not blocked merely because its head is
	/// awaiting execution.
	/// </para>
	/// <para>
	/// The candidate query below is a hint, not a decision: two workers may well read the
	/// same candidate. Only the conditional update decides, and a claim counts only if it
	/// changed exactly one row — which is what stops two workers both concluding a chain is
	/// clear and taking consecutive operations on the same message.
	/// </para>
	/// </remarks>
	public async Task<IReadOnlyList<MutationItem>> ClaimAsync(
		Guid accountId,
		string owner,
		TimeSpan leaseDuration,
		int max,
		CancellationToken ct = default
	)
	{
		var now = clock.GetUtcNow();
		var candidates = await context
			.MutationItems.Where(m =>
				m.AccountId == accountId
				&& (m.State == MutationState.Pending || m.State == MutationState.Leased)
			)
			.AsNoTracking()
			.ToListAsync(ct);

		// Ordered here rather than in SQL: SQLite cannot ORDER BY a DateTimeOffset, and the
		// order is a fairness preference, not part of the claim's correctness.
		candidates = [.. candidates.OrderBy(m => m.CreatedAt).ThenBy(m => m.Sequence)];

		var claimedIds = new List<Guid>();
		foreach (var candidate in candidates)
		{
			if (claimedIds.Count == max)
			{
				break;
			}

			// An expired lease makes an item claimable again. It does not imply the operation
			// did not happen — that question belongs to MutationExecutionAttempt, and an item
			// from a dispatched attempt with no persisted result must be reconciled rather
			// than re-executed.
			if (candidate.State == MutationState.Leased && candidate.LeaseExpiresAt >= now)
			{
				continue;
			}

			if (await TryClaimAsync(candidate, owner, now + leaseDuration, ct))
			{
				claimedIds.Add(candidate.Id);
			}
		}

		if (claimedIds.Count == 0)
		{
			return [];
		}

		context.ChangeTracker.Clear();
		var claimed = await context
			.MutationItems.Where(m => claimedIds.Contains(m.Id))
			.ToDictionaryAsync(m => m.Id, ct);
		return [.. claimedIds.Select(id => claimed[id])];
	}

	/// <summary>
	/// Releases claims that never crossed a new execution-attempt boundary. An item belonging
	/// to a dispatched or ambiguous attempt remains leased for reconciliation; completed or
	/// merely prepared historical memberships do not block a safe release.
	/// </summary>
	public async Task ReleaseUnattemptedAsync(
		Guid accountId,
		string owner,
		IEnumerable<Guid> itemIds,
		CancellationToken ct = default
	)
	{
		var ids = itemIds.Distinct().ToArray();
		if (ids.Length == 0)
		{
			return;
		}

		await context.MutationItems
			.Where(item =>
				ids.Contains(item.Id)
				&& item.AccountId == accountId
				&& item.State == MutationState.Leased
				&& item.LeaseOwner == owner
				&& !context.MutationExecutionAttemptItems.Any(
					membership =>
						membership.MutationItemId == item.Id
						&& context.MutationExecutionAttempts.Any(
							attempt =>
								attempt.Id == membership.AttemptId
								&& (attempt.State == MutationAttemptState.Dispatched
									|| attempt.State == MutationAttemptState.Ambiguous)
						)
				)
			)
			.ExecuteUpdateAsync(
				setters => setters
					.SetProperty(item => item.State, MutationState.Pending)
					.SetProperty(item => item.LeaseOwner, (string?)null)
					.SetProperty(item => item.LeaseExpiresAt, (DateTimeOffset?)null),
				ct
			);
		context.ChangeTracker.Clear();
	}

	private async Task<bool> TryClaimAsync(
		MutationItem candidate,
		string owner,
		DateTimeOffset expiry,
		CancellationToken ct
	)
	{
		var rows = await context.Database.ExecuteSqlRawAsync(
			ClaimSql,
			[
				// The Guid is passed as a Guid, not a string: the same driver conversion that
				// wrote the value has to produce the one being compared, and its text form is
				// upper-case where Guid.ToString() is not.
				new SqliteParameter("$id", candidate.Id),
				new SqliteParameter("$owner", owner),
				new SqliteParameter("$expiry", expiry),
				new SqliteParameter("$expectedState", (int)candidate.State),
				new SqliteParameter("$expectedOwner", (object?)candidate.LeaseOwner ?? DBNull.Value),
				new SqliteParameter("$leased", (int)MutationState.Leased),
				new SqliteParameter("$completed", (int)MutationState.Completed),
				new SqliteParameter("$failed", (int)MutationState.Failed),
				new SqliteParameter("$cancelled", (int)MutationState.Cancelled),
			],
			ct
		);

		return rows == 1;
	}
}
