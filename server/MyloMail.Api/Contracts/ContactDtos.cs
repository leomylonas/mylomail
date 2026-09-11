using Tapper;

namespace MyloMail.Api.Contracts;

[TranspilationSource]
public record ContactAddressDto(string Email);

[TranspilationSource]
public record ContactSuggestionDto(
	string DisplayName,
	IReadOnlyList<ContactAddressDto> Addresses
);

[TranspilationSource]
public record ContactDto(
	Guid Id,
	Guid AccountId,
	string DisplayName,
	IReadOnlyList<ContactAddressDto> Addresses,
	string? ProviderRevision,
	bool SyncConflict,
	bool SyncPending,
	bool AmbiguousOutcome,
	bool AmbiguousCreate,
	bool SyncRejected,
	bool CanDelete
);

[TranspilationSource]
public record SaveContactRequest(
	Guid? ContactId,
	Guid AccountId,
	string DisplayName,
	IReadOnlyList<string> Emails,
	string? ExpectedRevision
);

[TranspilationSource]
public record DeleteContactRequest(
	Guid ContactId,
	string? ExpectedRevision
);
