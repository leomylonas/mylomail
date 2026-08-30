namespace MyloMail.Api.Domain;

/// <summary>
/// The full parsed header set, as received. Read for the detail view and diagnostics only —
/// content lives in tables separate from <see cref="Message"/> so list views never
/// materialise large columns (§1).
/// </summary>
public class MessageHeaders
{
	public Guid MessageId { get; set; }

	/// <summary>Header name/value pairs in received order, preserving duplicates.</summary>
	public IReadOnlyList<MessageHeader> Headers { get; set; } = [];
}

public record MessageHeader(string Name, string Value);

/// <summary>Rendered bodies. <see cref="HtmlBody"/> is retained unchanged for rendering (§8).</summary>
public class MessageBody
{
	public Guid MessageId { get; set; }
	public string? TextBody { get; set; }
	public string? HtmlBody { get; set; }
}

/// <summary>
/// The original RFC 5322 bytes — the single stored representation of content (§1). There is
/// no attachment-blob table: attachments are extracted from here on demand, and <c>.eml</c>
/// export returns these bytes verbatim, preserving DKIM signatures, S/MIME and PGP parts.
/// </summary>
public class MessageRaw
{
	public Guid MessageId { get; set; }
	public byte[] Content { get; set; } = [];
}

/// <summary>
/// Content acquisition is message state, not mailbox state (§1): under Gmail's canonical
/// model one message belongs to several labels, so mailbox-scoped progress would fetch the
/// same message repeatedly and "65% indexed in Inbox" would be ill-defined.
/// </summary>
public class MessageContentState
{
	public Guid MessageId { get; set; }
	public ContentStatus Status { get; set; }

	/// <summary>
	/// Incremented whenever <see cref="MessageRaw"/> is replaced. Pairs with
	/// <see cref="Attachment.PartSpecifier"/>: a MIME part path is a locator within a
	/// particular stored blob, not an eternal identity.
	/// </summary>
	public int RawVersion { get; set; }

	public int Attempts { get; set; }
	public string? LastError { get; set; }
}

public enum ContentStatus
{
	NotFetched,
	Queued,
	Fetching,
	Indexed,
	Failed,
}

/// <summary>
/// Attachment metadata, parsed from raw at ingest so listing does not re-parse. Rows are
/// rebuilt whenever raw content is replaced (§1).
/// </summary>
public class Attachment
{
	public Guid Id { get; set; }
	public Guid MessageId { get; set; }

	/// <summary>
	/// MIME part path within <see cref="MessageRaw"/>, valid only for the
	/// <see cref="RawVersion"/> it was parsed from.
	/// </summary>
	public string PartSpecifier { get; set; } = string.Empty;

	/// <summary>The <see cref="MessageContentState.RawVersion"/> this locator applies to.</summary>
	public int RawVersion { get; set; }

	/// <summary>Untrusted input — sanitise before any filesystem use (§9).</summary>
	public string Filename { get; set; } = string.Empty;

	public string MimeType { get; set; } = string.Empty;
	public long Size { get; set; }

	/// <summary>For inline images referenced by <c>cid:</c>.</summary>
	public string? ContentId { get; set; }

	public bool IsInline { get; set; }
}

/// <summary>
/// The flattened FTS5 external-content source, 1:1 with <see cref="Message"/> (§8).
/// </summary>
/// <remarks>
/// <para>
/// <see cref="RowId"/> is an INTEGER key, not the Guid used elsewhere: FTS5 external-content
/// tables require a stable integer rowid, which a Guid cannot supply.
/// </para>
/// <para>
/// The columns are separate rather than one blob so FTS5's column-filter syntax supports
/// field-scoped search. HTML is indexed as extracted plain text, never markup, so tags and
/// inline CSS can neither match nor appear in results.
/// </para>
/// </remarks>
public class MessageSearchContent
{
	public long RowId { get; set; }
	public Guid MessageId { get; set; }
	public string Subject { get; set; } = string.Empty;
	public string BodyText { get; set; } = string.Empty;
	public string FromAddresses { get; set; } = string.Empty;
	public string ToAddresses { get; set; } = string.Empty;
	public string CcAddresses { get; set; } = string.Empty;
}
