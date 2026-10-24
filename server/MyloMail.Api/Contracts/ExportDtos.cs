using MyloMail.Api.Domain;
using Tapper;

namespace MyloMail.Api.Contracts;

/// <summary>An export, running or finished (§13 Export).</summary>
[TranspilationSource]
public record ExportJobDto(
	Guid Id,
	Guid AccountId,
	ExportJobStatus Status,
	ExportFormat Format,
	string DestinationPath,
	int WrittenCount,
	int TotalCount,
	int SkippedCount,
	string? LastError,
	DateTimeOffset CreatedAt,
	DateTimeOffset? CompletedAt
);

/// <summary>What the user chose in the export wizard.</summary>
[TranspilationSource]
public record StartExportRequestDto(
	Guid AccountId,
	IReadOnlyList<Guid> MailboxIds,
	ExportFormat Format,
	ExportFileNaming FileNaming,
	string DestinationPath
);

/// <summary>One folder offered in the wizard's tree, with what exporting it would involve.</summary>
[TranspilationSource]
public record ExportFolderDto(
	Guid MailboxId,
	Guid? ParentId,
	string Name,
	SpecialUse SpecialUse,
	int MessageCount,
	/// <summary>The size the provider reported for the mail in this folder, in bytes; messages with no reported size count as zero, so it is a floor.</summary>
	long SizeBytes,
	/// <summary>Messages on this computer whose full content is not yet downloaded; the export fetches them from the server as it goes.</summary>
	int NotDownloadedCount,
	/// <summary>False when older mail in this folder has not been downloaded to this computer, so the export will not include it.</summary>
	bool IsFullyDownloaded
);
