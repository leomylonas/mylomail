using Tapper;

namespace MyloMail.Api.Contracts;

/// <summary>How one account's manual "send &amp; receive" ended.</summary>
[TranspilationSource]
public enum SyncNowOutcome
{
	/// <summary>Every step ran; new changes, if any, have been applied.</summary>
	Synced,

	/// <summary>The provider is rate-limiting the account; nothing was asked of it.</summary>
	Throttled,

	/// <summary>The account is paused until the user signs in again.</summary>
	NeedsAttention,

	/// <summary>No network connection.</summary>
	Offline,

	/// <summary>Some step failed; the failure is in Problems and is retried automatically.</summary>
	Failed,
}

[TranspilationSource]
public record SyncNowAccountResultDto(Guid AccountId, string AccountName, SyncNowOutcome Outcome);

[TranspilationSource]
public record SyncNowResultDto(IReadOnlyList<SyncNowAccountResultDto> Accounts);
