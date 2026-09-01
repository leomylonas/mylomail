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
}

public enum ExportJobStatus
{
	Running,
	CancelRequested,
	Cancelled,
	Completed,
	Failed,
}
