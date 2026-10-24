using Tapper;

namespace MyloMail.Api.Domain;

/// <summary>
/// A bulk `.eml` export walking one account's mailbox tree (§13 Export).
/// </summary>
/// <remarks>
/// Progress is persisted, not merely broadcast — <see cref="ExportProgress"/> events are
/// derived from this row rather than being the state itself, so an export survives a restart
/// and can be cancelled and resumed like every other durable background job here.
/// </remarks>
public class ExportJob
{
	public Guid Id { get; set; }
	public Guid AccountId { get; set; }

	/// <summary>Absolute filesystem path the folder hierarchy is recreated under.</summary>
	public string DestinationPath { get; set; } = string.Empty;

	public ExportJobStatus Status { get; set; } = ExportJobStatus.Running;

	/// <summary>Occurrences to write — a message in five Gmail labels counts five times.</summary>
	public int TotalCount { get; set; }
	public int WrittenCount { get; set; }

	/// <summary>
	/// The occurrence ids to write, frozen at <see cref="ExportJobStatus.Running"/> creation
	/// time as a JSON array, in the fixed order the walk uses. `MessageMailbox.Id` is a random
	/// v4 GUID, not a sequential one — paging live against <c>ORDER BY Id</c> with a plain
	/// offset would silently skip or duplicate rows whenever mail arrived mid-export and shifted
	/// every later row's rank. Freezing the set once removes that dependency on the live table
	/// entirely: a resume re-reads the same list rather than re-deriving a shifting one.
	/// </summary>
	public string ManifestJson { get; set; } = "[]";

	/// <summary>
	/// How many entries of <see cref="ManifestJson"/> have already been attempted. Resuming
	/// after a restart or a batch failure skips past this rather than restarting the whole
	/// export — re-walking is safe either way, since writing a `.eml` file is idempotent, but
	/// pointless work at account scale is still worth avoiding.
	/// </summary>
	public int ResumeToken { get; set; }

	public string? LastError { get; set; }
	public DateTimeOffset CreatedAt { get; set; }

	public ExportFormat Format { get; set; } = ExportFormat.EmlFolders;
	public ExportFileNaming FileNaming { get; set; } = ExportFileNaming.MessageId;

	/// <summary>The mailboxes chosen, as a JSON array of ids. Messages are exported from these only.</summary>
	public string MailboxIdsJson { get; set; } = "[]";

	/// <summary>
	/// Manifest entries that could not be written: the message was moved or deleted after the
	/// manifest was made, or the file could not be created.
	/// </summary>
	public int SkippedCount { get; set; }

	/// <summary>
	/// For mbox: each file's length (JSON object, mailbox id to bytes) as of the last manifest
	/// position that was saved. A batch that died after appending but before saving is undone by
	/// cutting the file back to this length, so a message never appears twice.
	/// </summary>
	public string MboxOffsetsJson { get; set; } = "{}";

	/// <summary>
	/// How many times the manifest was extended with mail that arrived while the export ran. The
	/// export is complete when a pass over the chosen folders finds nothing new; that moment is
	/// <see cref="CompletedAt"/>, and mail arriving after it is not in the export.
	/// </summary>
	public int CatchUpPasses { get; set; }

	/// <summary>When the export ended, however it ended.</summary>
	public DateTimeOffset? CompletedAt { get; set; }
}

[TranspilationSource]
public enum ExportFormat
{
	/// <summary>A folder tree matching the mailboxes, one <c>.eml</c> file per message.</summary>
	EmlFolders,

	/// <summary>One mbox file per mailbox, nested the way Thunderbird does (<c>Name</c> and <c>Name.sbd/</c>).</summary>
	Mbox,
}

[TranspilationSource]
public enum ExportFileNaming
{
	MessageId,
	DateAndSubject,
}

[TranspilationSource]
public enum ExportJobStatus
{
	Running,
	CancelRequested,
	Cancelled,
	Completed,
	Failed,
}
