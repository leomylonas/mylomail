namespace MyloMail.Api.Credentials;

/// <summary>Signals Electron to prompt for a master password and restart the backend.</summary>
public sealed class CredentialStoreUnavailableException : Exception
{
	public const int ExitCode = 78;
	public CredentialStoreUnavailableException()
		: base("No native credential store is available and no master password was supplied.") { }
}
