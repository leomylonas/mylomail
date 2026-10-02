using Tapper;

namespace MyloMail.Api.Contracts;

/// <summary>
/// What a mailbox listing is ordered by. The server orders the whole mailbox before taking a
/// page: ordering only the pages already loaded would make "oldest first" mean the oldest of the
/// current window, not of the mailbox.
/// </summary>
[TranspilationSource]
public enum MessageSortField
{
	Date,
	From,
	Subject,
	Snippet,
	Read,
	Flag,
}
