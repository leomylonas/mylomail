using MyloMail.Api.Domain;

namespace MyloMail.Api.Providers;

public sealed record ProviderContact(
	string ProviderContactId,
	string? Revision,
	string DisplayName,
	IReadOnlyList<string> Emails,
	string? ProviderContainerId = null,
	IReadOnlyList<string>? PreviousProviderContactIds = null
);
public sealed record ContactPullResult(
	IReadOnlyList<ProviderContact> Contacts,
	IReadOnlyList<string> DeletedProviderContactIds,
	string? NextCursor,
	bool IsFullSnapshot
);
public sealed record ProviderContactWrite(string DisplayName, IReadOnlyList<string> Emails);
public sealed class ProviderContactRejectedException(string message) : Exception(message);


/// <summary>Contact providers absorb remote/API differences; IMAP is deliberately local-only.</summary>
public interface IContactProvider
{
	Task<ContactPullResult> PullAsync(Account account, bool useCursor, CancellationToken ct);
	Task<ProviderContact> CreateAsync(Account account, ProviderContactWrite contact, CancellationToken ct);
	Task<ProviderContact> UpdateAsync(Account account, string providerContactId, string? providerContainerId, string? expectedRevision, ProviderContactWrite contact, CancellationToken ct);
	Task DeleteAsync(Account account, string providerContactId, string? providerContainerId, string? expectedRevision, CancellationToken ct);
}

public interface IContactProviderFactory
{
	IContactProvider For(Account account);
}
