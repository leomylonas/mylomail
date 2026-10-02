using Tapper;

namespace MyloMail.Api.Contracts;

/// <summary>
/// Narrows a mailbox listing before it is paged. Applied by the server across the whole mailbox:
/// a filter applied only to the pages already loaded would hide a match that simply had not been
/// loaded yet and make a filtered list look shorter than it is.
/// </summary>
/// <param name="Text">Case-insensitive; matches subject, snippet or sender name/address. Null or blank matches all.</param>
/// <param name="ReceivedFrom">Inclusive lower bound. The client supplies it because "today" is the reader's local midnight.</param>
/// <param name="ReceivedBefore">Exclusive upper bound.</param>
/// <param name="IsRead">Null for either; true for read only; false for unread only.</param>
/// <param name="IsFlagged">Null for either; true for flagged only; false for unflagged only.</param>
[TranspilationSource]
public record MessageListFilterDto(
	string? Text,
	DateTimeOffset? ReceivedFrom,
	DateTimeOffset? ReceivedBefore,
	bool? IsRead,
	bool? IsFlagged
)
{
	public bool Matches(Domain.Message message)
	{
		if (IsRead is { } read && message.IsRead != read) return false;
		if (IsFlagged is { } flagged && message.IsFlagged != flagged) return false;
		if (ReceivedFrom is { } from && message.ReceivedAt < from) return false;
		if (ReceivedBefore is { } before && message.ReceivedAt >= before) return false;
		if (Text?.Trim() is not { Length: > 0 } needle) return true;

		static bool Has(string? haystack, string needle) =>
			haystack?.Contains(needle, StringComparison.CurrentCultureIgnoreCase) == true;
		return Has(message.Subject, needle)
			|| Has(message.Snippet, needle)
			|| message.From.Any(sender => Has(sender.Name, needle) || Has(sender.Email, needle));
	}
}
