using System.Text.RegularExpressions;
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
	/// <c>Subject:invoice</c> works directly against the index's own column name, but
	/// <c>from:</c>/<c>to:</c>/<c>cc:</c>/<c>body:</c> — the field names Epic 5 actually
	/// documents — do not, since the indexed columns are named <c>FromAddresses</c>,
	/// <c>ToAddresses</c>, <c>CcAddresses</c> and <c>BodyText</c>. FTS5 column-filter syntax
	/// requires an exact column-name match with no aliasing, so before this rewrite those
	/// prefixes threw a SQLite syntax error caught below and surfaced as a silent zero-result
	/// search — not a crash, but not the documented feature either. <see cref="RewriteFieldPrefixes"/>
	/// translates the documented short forms to their real column names before the query
	/// reaches FTS5. A malformed query beyond that is still the user's mistake to see, not an
	/// exception to leak.
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

		var matchedIds = matched.Select(x => x.Id).ToHashSet();
		var messages = await context
			.Messages.Where(m => m.AccountId == accountId && matchedIds.Contains(m.Id))
			.Where(m =>
				mailboxId == null
				|| context.MessageMailboxes.Any(o => o.MessageId == m.Id && o.MailboxId == mailboxId)
			)
			.ToListAsync(ct);

		// Relevance order, not recency: `matched` already carries FTS5's own `rank` order,
		// but the EF `Contains` query above doesn't preserve it, so it's reapplied here
		// in-memory (SQLite cannot ORDER BY a DateTimeOffset either, which ruled out doing
		// this as part of the query in the first place).
		var rank = matched.Select((x, index) => (x.Id, index)).ToDictionary(x => x.Id, x => x.index);
		var snippets = matched.ToDictionary(x => x.Id, x => x.Snippet);
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
				failures.TryGetValue(m.Id, out var category) ? category : null,
				snippets[m.Id]
			)),
		];
	}

	// Matched-term markers for MessageSummaryDto.SearchSnippet: ASCII SOH/STX, never valid in
	// ordinary message text and never HTML, so the renderer can split on them safely without
	// risking a sender's own content being mistaken for markup (or, worse, actually rendered
	// as some).
	private const string HighlightStart = "\u0001";
	private const string HighlightEnd = "\u0002";

	// Matches a field-filter prefix at the start of the query or right after whitespace/an
	// opening paren (FTS5's own column-filter position), so "from:" inside quoted body text
	// (a search for the literal word) is left alone — only a genuine filter position is rewritten.
	private static readonly Regex FieldPrefixPattern = new(
		@"(?<=^|[\s(])(from|to|cc|body)(?=:)",
		RegexOptions.IgnoreCase | RegexOptions.Compiled
	);

	/// <summary>
	/// Rewrites Epic 5's documented short field names to the search index's actual FTS5 column
	/// names, so <c>from:alice</c> matches <c>FromAddresses</c> instead of failing with "no such
	/// column: from" (silently swallowed by <see cref="MatchAsync"/>'s syntax-error catch, since
	/// FTS5 offers no column aliasing of its own).
	/// </summary>
	internal static string RewriteFieldPrefixes(string query) =>
		FieldPrefixPattern.Replace(
			query,
			m =>
				m.Value.ToLowerInvariant() switch
				{
					"from" => "FromAddresses",
					"to" => "ToAddresses",
					"cc" => "CcAddresses",
					"body" => "BodyText",
					_ => m.Value,
				}
		);

	// Matches one query token: an optional column-filter prefix ("FromAddresses:") glued
	// directly to either an already-quoted phrase or a bareword run of non-whitespace.
	private static readonly Regex TokenPattern = new(
		@"(?<prefix>[A-Za-z]+:)?(?<term>""[^""]*""|\S+)",
		RegexOptions.Compiled
	);

	// What FTS5 accepts as a bareword with no quoting needed. Anything else — most
	// significantly "@" and "." from an email address, the single most natural thing to
	// search a mail client for — is a syntax character to FTS5's own query grammar, not
	// just its tokenizer: unquoted, it throws rather than merely failing to match.
	private static readonly Regex SafeBarewordPattern = new(@"^[\w*]+$", RegexOptions.Compiled);

	/// <summary>
	/// Quotes any bareword token FTS5's query grammar would otherwise choke on. An address like
	/// <c>alice@example.com</c> — or anything else containing <c>@</c>, <c>.</c>, <c>-</c> and
	/// several other punctuation characters — is a syntax error to FTS5 when unquoted, not just a
	/// tokenizer mismatch; <see cref="MatchAsync"/>'s syntax-error catch then turns that into a
	/// silent, empty result set for the single most natural "from:" query anyone would type. Runs
	/// after <see cref="RewriteFieldPrefixes"/> so a field prefix's colon is still recognised, and
	/// leaves an already-quoted phrase (or the deliberately-unbalanced-quote case a malformed
	/// query already covers) untouched.
	/// </summary>
	internal static string SanitizeForFts5(string query) =>
		TokenPattern.Replace(
			query,
			m =>
			{
				var term = m.Groups["term"].Value;
				if (term.StartsWith('"') || SafeBarewordPattern.IsMatch(term))
				{
					return m.Value;
				}
				return $"{m.Groups["prefix"].Value}\"{term.Replace("\"", "\"\"")}\"";
			}
		);

	/// <summary>The message ids FTS5 matches, in relevance order, with a match-context excerpt.</summary>
	private async Task<List<(Guid Id, string Snippet)>> MatchAsync(string query, CancellationToken ct)
	{
		var rewritten = SanitizeForFts5(RewriteFieldPrefixes(query));

		await context.Database.OpenConnectionAsync(ct);
		await using var command = context.Database.GetDbConnection().CreateCommand();

		// Column index -1 lets FTS5 pick whichever indexed column (Subject, BodyText, ...)
		// actually contains the match, rather than assuming it's always the body.
		command.CommandText = """
			SELECT c."MessageId", snippet("MessageSearchIndex", -1, $start, $end, $ellipsis, $maxTokens)
			FROM "MessageSearchIndex" AS i
			JOIN "MessageSearchContents" AS c ON c."RowId" = i."rowid"
			WHERE "MessageSearchIndex" MATCH $query
			ORDER BY rank;
			""";
		command.Parameters.Add(new SqliteParameter("$query", rewritten));
		command.Parameters.Add(new SqliteParameter("$start", HighlightStart));
		command.Parameters.Add(new SqliteParameter("$end", HighlightEnd));
		command.Parameters.Add(new SqliteParameter("$ellipsis", "…"));
		command.Parameters.Add(new SqliteParameter("$maxTokens", 20));

		// A List, not a Dictionary: relevance order is FTS5's own read order here, and a
		// List's enumeration order is an actual language guarantee, unlike a Dictionary's
		// insertion order, which is an implementation detail the runtime has never promised
		// to keep.
		var matches = new List<(Guid Id, string Snippet)>();
		try
		{
			await using var reader = await command.ExecuteReaderAsync(ct);
			while (await reader.ReadAsync(ct))
			{
				matches.Add((reader.GetGuid(0), reader.GetString(1)));
			}
		}
		catch (SqliteException ex) when (ex.SqliteErrorCode == 1)
		{
			// A syntax error in the user's own query. Returning nothing is the honest answer;
			// an exception here would surface as a crash for a mistyped quote.
			return [];
		}

		return matches;
	}
}
