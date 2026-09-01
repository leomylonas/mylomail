using MyloMail.Api.Domain;
using Tapper;

namespace MyloMail.Api.Contracts;

/// <summary>An account's most recent bulk export, whatever its status (§13 Export).</summary>
[TranspilationSource]
public record ExportJobDto(
	Guid Id,
	ExportJobStatus Status,
	int WrittenCount,
	int TotalCount,
	string? LastError
);
