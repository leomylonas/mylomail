namespace MyloMail.Api.Providers;

/// <summary>
/// Thrown when a provider cursor can no longer be used: IMAP <c>UIDVALIDITY</c> change,
/// Gmail <c>historyId</c> expiry (404 on <c>users.history.list</c>), Graph <c>410 Gone</c>
/// on a delta link.
/// </summary>
/// <remarks>
/// Every provider can invalidate a cursor and all need the same response — discard it,
/// mark the mailbox for triggered resynchronisation, and re-establish a baseline. Raising
/// one exception type is what lets that be handled once rather than three times (§3).
/// Providers must translate their native signal into this rather than surfacing a
/// provider-specific error.
/// </remarks>
public class ProviderCursorInvalidException : Exception
{
	public ProviderCursorInvalidException(string message)
		: base(message) { }

	public ProviderCursorInvalidException(string message, Exception innerException)
		: base(message, innerException) { }
}
