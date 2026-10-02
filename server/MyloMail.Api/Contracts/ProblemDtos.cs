using Tapper;

namespace MyloMail.Api.Contracts;

[TranspilationSource]
public enum ProblemKind
{
	/// <summary>A message whose content could not be downloaded and that has been given up on.</summary>
	MessageDownload,

	/// <summary>
	/// A message larger than the account's download limit. Retrying cannot help; raising the
	/// limit in the account's settings can, so the UI offers that instead.
	/// </summary>
	MessageTooLarge,

	/// <summary>
	/// A message that failed before and is queued to be tried again. Not a problem yet: it
	/// becomes <see cref="MessageDownload"/> only if it fails again.
	/// </summary>
	MessageRetrying,

	/// <summary>A folder whose sync, change tracking or integrity check is failing.</summary>
	FolderSync,
}

/// <summary>
/// Something background work could not do, with enough context for the user to act on it.
/// </summary>
/// <remarks>
/// Deliberately one flat shape for every kind: the UI is a single list. <paramref name="Detail"/>
/// is the underlying error text as recorded, unmapped — it is diagnostic and shown as such.
/// The message fields are set for <see cref="ProblemKind.MessageDownload"/> only; the folder
/// fields identify where it happened for both kinds.
/// </remarks>
[TranspilationSource]
public record ProblemDto(
	string Id,
	ProblemKind Kind,
	Guid AccountId,
	string Title,
	string? Detail,
	Guid? MailboxId,
	string? MailboxName,
	Guid? MessageId,
	string? Subject,
	string? From,
	DateTimeOffset? ReceivedAt,
	int? Attempts,
	bool CanRetry
);
