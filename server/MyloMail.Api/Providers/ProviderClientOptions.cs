namespace MyloMail.Api.Providers;

/// <summary>
/// The non-secret client registration each OAuth provider needs before it can authenticate
/// anyone (§5).
/// </summary>
/// <remarks>
/// These identify <i>this application</i> to the provider, not any user, so they are
/// configuration rather than credentials and never go through <c>ICredentialStore</c>. Gmail's
/// client secret is the awkward case: §5 records that distributing it in a desktop binary is
/// unresolved, and installed-app flows treat it as non-confidential, so it is configuration
/// here too — but it must still be supplied per deployment rather than committed.
/// </remarks>
public sealed class ProviderClientOptions
{
	public const string SectionName = "Providers";

	public GmailClientOptions Gmail { get; set; } = new();

	public GraphClientOptions Graph { get; set; } = new();
}

public sealed class GmailClientOptions
{
	public string? ClientId { get; set; }
	public string? ClientSecret { get; set; }

	public bool IsConfigured => !string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(ClientSecret);
}

public sealed class GraphClientOptions
{
	public string? ClientId { get; set; }

	/// <summary>Defaults to the multi-tenant consumer-and-work authority.</summary>
	public string Authority { get; set; } = "https://login.microsoftonline.com/common";

	public bool IsConfigured => !string.IsNullOrWhiteSpace(ClientId);
}
