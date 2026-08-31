using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using MimeKit;
using MyloMail.Api.Domain;
using MyloMail.Api.Persistence;

namespace MyloMail.Api.Content;

/// <summary>
/// Maintains the flattened search row and its FTS5 index (§8).
/// </summary>
/// <remarks>
/// <para>
/// The index is external-content over <c>MessageSearchContents</c>, and is maintained by
/// explicit upsert rather than by SQL triggers: triggers are fiddly against external-content
/// tables and invisible to EF Core, so insert, update and delete would each be a behaviour
/// nobody could see in the model.
/// </para>
/// <para>
/// <b>HTML is indexed as extracted text, never markup.</b> Indexing tags and inline CSS would
/// let a search for a common attribute name match every message in the mailbox.
/// </para>
/// </remarks>
public sealed class SearchIndexer(MyloMailDbContext context)
{
	public async Task IndexAsync(Guid messageId, MimeMessage mime, MessageBody body, CancellationToken ct)
	{
		var message = await context.Messages.FirstAsync(m => m.Id == messageId, ct);
		var row = await context.MessageSearchContents.FirstOrDefaultAsync(c => c.MessageId == messageId, ct);

		// Captured *before* the row is changed. An external-content index is updated by
		// deleting the terms it currently holds, and FTS5 is told those terms by being handed
		// the old column values back. Handing it the new ones deletes terms that were never
		// indexed and leaves the index inconsistent with its content table — which SQLite
		// then reports, confusingly, as "database disk image is malformed".
		var indexed = row is null ? null : Snapshot(row);

		if (row is null)
		{
			row = new MessageSearchContent { MessageId = messageId };
			context.MessageSearchContents.Add(row);
		}

		row.Subject = message.Subject;
		row.BodyText = PlainText(body);
		row.FromAddresses = Flatten(mime.From);
		row.ToAddresses = Flatten(mime.To);
		row.CcAddresses = Flatten(mime.Cc);

		// Flushed so a new row has its integer key: FTS5 external-content tables address rows
		// by rowid, which is why this table carries one at all (§8).
		await context.SaveChangesAsync(ct);

		if (indexed is not null)
		{
			await DeleteFromIndexAsync(indexed, ct);
		}

		await InsertIntoIndexAsync(row, ct);
	}

	private static MessageSearchContent Snapshot(MessageSearchContent row) =>
		new()
		{
			RowId = row.RowId,
			MessageId = row.MessageId,
			Subject = row.Subject,
			BodyText = row.BodyText,
			FromAddresses = row.FromAddresses,
			ToAddresses = row.ToAddresses,
			CcAddresses = row.CcAddresses,
		};

	/// <summary>Removes a message from the index, as part of tombstone collection (§6).</summary>
	public async Task RemoveAsync(Guid messageId, CancellationToken ct)
	{
		var row = await context.MessageSearchContents.FirstOrDefaultAsync(c => c.MessageId == messageId, ct);
		if (row is null)
		{
			return;
		}

		// Deleted from the index before the content it mirrors, or search returns hits
		// pointing at nothing.
		await DeleteFromIndexAsync(row, ct);

		context.MessageSearchContents.Remove(row);
		await context.SaveChangesAsync(ct);
	}

	/// <summary>
	/// Removes every message of one account from the index and its content table.
	/// </summary>
	/// <remarks>
	/// Bulk removal rebuilds rather than deleting term by term. A per-row delete has to hand
	/// FTS5 each row's old values back exactly, and getting one wrong corrupts the index
	/// silently; a rebuild reconstructs the whole index from the content table, which is the
	/// authority. Account removal is rare enough that the cost does not matter, and being
	/// unconditionally correct here does.
	/// </remarks>
	public async Task RemoveForAccountAsync(Guid accountId, CancellationToken ct)
	{
		var removed = await context
			.MessageSearchContents.Where(content =>
				context.Messages.Any(m => m.Id == content.MessageId && m.AccountId == accountId)
			)
			.ExecuteDeleteAsync(ct);

		if (removed > 0)
		{
			await RebuildAsync(ct);
		}
	}

	/// <summary>
	/// Rebuilds the index from the content table, discarding whatever it held.
	/// </summary>
	/// <remarks>
	/// <b>This loses no mail.</b> The index is derived data: every indexed term comes from
	/// <c>MessageSearchContents</c>, which is itself derived from stored message content. It
	/// is the recovery path for an index that has become inconsistent — the one repair that
	/// cannot make things worse, because it does not read the index it is replacing.
	/// </remarks>
	public Task RebuildAsync(CancellationToken ct) =>
		context.Database.ExecuteSqlRawAsync(
			"INSERT INTO \"MessageSearchIndex\"(\"MessageSearchIndex\") VALUES ('rebuild');",
			ct
		);

	/// <summary>
	/// Whether the index still matches its content table.
	/// </summary>
	/// <remarks>
	/// FTS5's own check. Worth asking because an inconsistency is silent where it is created
	/// and surfaces much later as a write failing with "database disk image is malformed".
	/// </remarks>
	public async Task<bool> IsIntactAsync(CancellationToken ct)
	{
		try
		{
			// The `1` matters: without it FTS5 only checks the index against itself, and an
			// index that disagrees with its content table — the failure mode that actually
			// happens here — passes.
			await context.Database.ExecuteSqlRawAsync(
				"INSERT INTO \"MessageSearchIndex\"(\"MessageSearchIndex\", \"rank\") VALUES ('integrity-check', 1);",
				ct
			);
			return true;
		}
		catch (SqliteException)
		{
			return false;
		}
	}

	private Task DeleteFromIndexAsync(MessageSearchContent indexed, CancellationToken ct) =>
		context.Database.ExecuteSqlRawAsync(
			"""
			INSERT INTO "MessageSearchIndex"("MessageSearchIndex", "rowid", "Subject", "BodyText", "FromAddresses", "ToAddresses", "CcAddresses")
			VALUES ('delete', $rowid, $subject, $body, $from, $to, $cc);
			""",
			Parameters(indexed),
			ct
		);

	private Task InsertIntoIndexAsync(MessageSearchContent row, CancellationToken ct) =>
		context.Database.ExecuteSqlRawAsync(
			"""
			INSERT INTO "MessageSearchIndex"("rowid", "Subject", "BodyText", "FromAddresses", "ToAddresses", "CcAddresses")
			VALUES ($rowid, $subject, $body, $from, $to, $cc);
			""",
			Parameters(row),
			ct
		);

	private static SqliteParameter[] Parameters(MessageSearchContent row) =>
		[
			new SqliteParameter("$rowid", row.RowId),
			new SqliteParameter("$subject", row.Subject),
			new SqliteParameter("$body", row.BodyText),
			new SqliteParameter("$from", row.FromAddresses),
			new SqliteParameter("$to", row.ToAddresses),
			new SqliteParameter("$cc", row.CcAddresses),
		];

	/// <summary>
	/// The plain-text body, preferring the text alternative and stripping markup otherwise.
	/// </summary>
	private static string PlainText(MessageBody body)
	{
		if (!string.IsNullOrWhiteSpace(body.TextBody))
		{
			return body.TextBody;
		}

		return string.IsNullOrWhiteSpace(body.HtmlBody) ? string.Empty : StripMarkup(body.HtmlBody);
	}

	/// <summary>
	/// Removes tags, script and style content so only readable text is indexed.
	/// </summary>
	/// <remarks>
	/// Deliberately crude and deliberately not a parser: this feeds a search index, where a
	/// stray character costs a slightly worse match, whereas parsing untrusted HTML to render
	/// it is a different job with different stakes. Rendering uses the original markup.
	/// </remarks>
	private static string StripMarkup(string html)
	{
		var text = new StringBuilder(html.Length);
		var depth = 0;
		var skipping = false;

		for (var i = 0; i < html.Length; i++)
		{
			if (html[i] == '<')
			{
				var tag = html.AsSpan(i);
				skipping =
					tag.StartsWith("<script", StringComparison.OrdinalIgnoreCase)
					|| tag.StartsWith("<style", StringComparison.OrdinalIgnoreCase)
					|| (skipping && !tag.StartsWith("</script", StringComparison.OrdinalIgnoreCase)
						&& !tag.StartsWith("</style", StringComparison.OrdinalIgnoreCase));
				depth++;
				continue;
			}

			if (html[i] == '>')
			{
				depth = Math.Max(0, depth - 1);
				text.Append(' ');
				continue;
			}

			if (depth == 0 && !skipping)
			{
				text.Append(html[i]);
			}
		}

		return string.Join(' ', text.ToString().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
	}

	private static string Flatten(InternetAddressList addresses) =>
		string.Join(" ", addresses.Mailboxes.Select(m => $"{m.Name} {m.Address}".Trim()));
}
