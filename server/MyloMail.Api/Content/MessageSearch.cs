using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using MyloMail.Api.Contracts;
using MyloMail.Api.Mutations;
using MyloMail.Api.Persistence;

namespace MyloMail.Api.Content;

/// <summary>
/// Full-text search over stored messages (§8).
/// </summary>
public sealed class MessageSearch(MyloMailDbContext context)
{
	/// <summary>
	/// Matches first, then scopes.
	/// </summary>
	/// <remarks>
	/// <para>
	/// <b>Mailbox membership is not indexed.</b> Scoping is a join onto <c>MessageMailbox</c>
	/// applied after the FTS match, which is what lets a message move folders without ever
	/// being reindexed — and moving is one of the commonest things that happens to mail.
	/// </para>
	/// <para>
	/// The query is passed to FTS5 as a parameter, so its syntax is the user's to use:
	/// <c>Subject:invoice</c> and <c>from:alice</c> work because the index has real columns.
	/// A malformed query is the user's mistake to see, not an exception to leak.
	/// </para>
	/// </remarks>
	public async Task<IReadOnlyList<MessageSummaryDto>> SearchAsync(
		Guid accountId,
		string query,
		Guid? mailboxId = null,
		int take = 50,
		CancellationToken ct = default
	)
	{
		if (string.IsNullOrWhiteSpace(query))
		{
			return [];
		}

		var matched = await MatchAsync(query, ct);
		if (matched.Count == 0)
		{
			return [];
		}

		var messages = await context
			.Messages.Where(m => m.AccountId == accountId && matched.Contains(m.Id))
			.Where(m =>
				mailboxId == null
				|| context.MessageMailboxes.Any(o => o.MessageId == m.Id && o.MailboxId == mailboxId)
			)
			.ToListAsync(ct);

		// Relevance order, not recency: `matched` already carries FTS5's own `rank` order,
		// but the EF `Contains` query above doesn't preserve it, so it's reapplied here
		// in-memory (SQLite cannot ORDER BY a DateTimeOffset either, which ruled out doing
		// this as part of the query in the first place).
		var rank = matched.Select((id, index) => (id, index)).ToDictionary(x => x.id, x => x.index);
		var shown = messages.OrderBy(m => rank[m.Id]).Take(take).ToList();
		var failures = await MessageMutationFailures.ForMessagesAsync(
			context,
			shown.Select(m => m.Id).ToList(),
			ct
		);

		return
		[
			.. shown.Select(m => new MessageSummaryDto(
				m.Id,
				m.AccountId,
				m.Subject,
				m.Snippet,
				m.From,
				m.ReceivedAt,
				m.IsRead,
				m.IsFlagged,
				m.HasNonInlineAttachments,
				failures.TryGetValue(m.Id, out var category) ? category : null
			)),
		];
	}

	/// <summary>The message ids FTS5 matches, in relevance order.</summary>
	private async Task<List<Guid>> MatchAsync(string query, CancellationToken ct)
	{
		await context.Database.OpenConnectionAsync(ct);
		await using var command = context.Database.GetDbConnection().CreateCommand();

		command.CommandText = """
			SELECT c."MessageId"
			FROM "MessageSearchIndex" AS i
			JOIN "MessageSearchContents" AS c ON c."RowId" = i."rowid"
			WHERE "MessageSearchIndex" MATCH $query
			ORDER BY rank;
			""";
		command.Parameters.Add(new SqliteParameter("$query", query));

		var ids = new List<Guid>();
		try
		{
			await using var reader = await command.ExecuteReaderAsync(ct);
			while (await reader.ReadAsync(ct))
			{
				ids.Add(reader.GetGuid(0));
			}
		}
		catch (SqliteException ex) when (ex.SqliteErrorCode == 1)
		{
			// A syntax error in the user's own query. Returning nothing is the honest answer;
			// an exception here would surface as a crash for a mistyped quote.
			return [];
		}

		return ids;
	}
}
