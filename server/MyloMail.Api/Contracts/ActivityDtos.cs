using MyloMail.Api.Domain;
using Tapper;

namespace MyloMail.Api.Contracts;

/// <summary>
/// What background download work is doing right now, account by account, for the status bar's
/// details dialog: what is downloading, what is waiting, what is held back and why.
/// </summary>
[TranspilationSource]
public record ActivityDto(IReadOnlyList<AccountActivityDto> Accounts);

/// <param name="ThrottleRetryAfterSeconds">Set while the provider is rate-limiting this account.</param>
/// <param name="Walk">Gmail's account-wide download (null for other providers, which download per folder).</param>
/// <param name="Folders">Folders still being downloaded or stuck, never finished ones.</param>
/// <param name="FolderCount">Every real folder of the account, so finished work can be summarised.</param>
/// <param name="StagedHistoryPages">Gmail changes received during the download and applied when it finishes.</param>
/// <param name="Downloading">Messages whose content is being fetched right now.</param>
/// <param name="FailureGroups">Failed content downloads grouped by their recorded error.</param>
[TranspilationSource]
public record AccountActivityDto(
	Guid AccountId,
	string AccountName,
	ProviderType Provider,
	int? ThrottleRetryAfterSeconds,
	WalkActivityDto? Walk,
	IReadOnlyList<FolderActivityDto> Folders,
	int FolderCount,
	int StagedHistoryPages,
	int ContentReady,
	int ContentQueued,
	int ContentFetching,
	int ContentFailed,
	IReadOnlyList<ActivityMessageDto> Downloading,
	IReadOnlyList<ActivityFailureGroupDto> FailureGroups
);

[TranspilationSource]
public record WalkActivityDto(CoverageStatus Status, int Fetched, int? EstimatedTotal, string? LastError);

[TranspilationSource]
public record FolderActivityDto(
	string Name,
	CoverageStatus Status,
	int Fetched,
	int? EstimatedTotal,
	string? LastError
);

[TranspilationSource]
public record ActivityMessageDto(Guid MessageId, string Subject, string? From, int Attempts);

[TranspilationSource]
public record ActivityFailureGroupDto(string Error, int Count);

/// <summary>
/// One piece of background work for the status bar: what it is, whose it is, and how far along it
/// is when that can be known.
/// </summary>
/// <param name="Label">What is happening, in the user's terms.</param>
/// <param name="AccountName">The account it is for, when it is for one.</param>
/// <param name="Done">Items finished so far; null when progress cannot be measured.</param>
/// <param name="Total">Items in all; null when unknown.</param>
/// <param name="StartedAt">When it began, when that is known (queued work has no start).</param>
/// <param name="Running">True while it is being worked on; false while it is queued behind other work.</param>
[TranspilationSource]
public record BackgroundTaskDto(
	string Label,
	string? AccountName,
	int? Done,
	int? Total,
	DateTimeOffset? StartedAt,
	bool Running
);
