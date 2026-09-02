using Microsoft.EntityFrameworkCore;
using MyloMail.Api.Domain;
using MyloMail.Api.Errors;
using MyloMail.Api.Persistence;

namespace MyloMail.Api.Mutations;

/// <summary>
/// Looks up which messages should show a durable failure indicator (§13 Epic 4) — the highest
/// -<see cref="MutationItem.Sequence"/> <b>resolved</b> (terminal) item per message, if and
/// only if it ended failed or cancelled. Self-clearing: a later, successful mutation for the
/// same message has a higher sequence and simply outranks the failed one once it too resolves,
/// with no separate flag to reset. A newer mutation that is merely queued (<c>Pending</c>/
/// <c>Leased</c>) does not count as resolving anything — it is skipped, so an existing failure
/// keeps showing until whatever superseded it actually finishes, not the moment it is enqueued.
/// </summary>
public static class MessageMutationFailures
{
	public static async Task<IReadOnlyDictionary<Guid, ErrorCategory>> ForMessagesAsync(
		MyloMailDbContext context,
		IReadOnlyCollection<Guid> messageIds,
		CancellationToken ct = default
	)
	{
		if (messageIds.Count == 0)
		{
			return new Dictionary<Guid, ErrorCategory>();
		}

		var items = await context
			.MutationItems.Where(m => messageIds.Contains(m.MessageId))
			.Select(m => new
			{
				m.MessageId,
				m.Sequence,
				m.State,
				m.FailureCategory,
			})
			.ToListAsync(ct);

		var result = new Dictionary<Guid, ErrorCategory>();
		foreach (var group in items.GroupBy(m => m.MessageId))
		{
			var latestResolved = group
				.OrderByDescending(m => m.Sequence)
				.FirstOrDefault(m =>
					m.State is MutationState.Completed or MutationState.Failed or MutationState.Cancelled
				);
			if (
				latestResolved is { State: MutationState.Failed or MutationState.Cancelled, FailureCategory: { } category }
			)
			{
				result[group.Key] = category;
			}
		}
		return result;
	}
}
