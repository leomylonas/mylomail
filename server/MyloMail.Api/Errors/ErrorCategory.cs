namespace MyloMail.Api.Errors;

/// <summary>
/// The single error taxonomy for the system (§15). Every failure — provider, sync,
/// mutation or transport — is categorised into one of these; the category is what drives
/// UI behaviour, so a new error must be mapped into this enum rather than escaping as a
/// flat string.
/// </summary>
public enum ErrorCategory
{
	Network,
	Auth,
	RateLimit,
	Validation,
	ProviderRejected,
	Conflict,
	Unknown,
}
