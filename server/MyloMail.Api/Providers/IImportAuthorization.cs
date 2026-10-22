using MyloMail.Api.Domain;

namespace MyloMail.Api.Providers;

/// <summary>
/// A provider whose way of storing a received message needs a permission the account's sign-in does
/// not otherwise carry. Asked for when the user starts a move into the account (so a consent screen
/// has someone to answer it), never from a background job.
/// </summary>
public interface IImportAuthorization
{
	/// <exception cref="InvalidOperationException">The permission was declined, or could not be granted; the message says why, for the user.</exception>
	Task EnsureImportAuthorizedAsync(Account account, CancellationToken ct);
}
