using Google.Apis.Auth.OAuth2;

namespace MyloMail.Api.Credentials;

/// <summary>
/// Preserves the normal loopback OAuth flow while making a desktop-launch failure recoverable.
/// </summary>
internal sealed class DiagnosticsLocalServerCodeReceiver : LocalServerCodeReceiver
{
	private const string AuthorizationUrlPathVariable = "MYLOMAIL_OAUTH_DIAGNOSTICS_PATH";

	protected override bool OpenBrowser(string url)
	{
		var diagnosticPath = Environment.GetEnvironmentVariable(AuthorizationUrlPathVariable);
		if (!string.IsNullOrWhiteSpace(diagnosticPath))
		{
			Directory.CreateDirectory(Path.GetDirectoryName(diagnosticPath) ?? ".");
			File.WriteAllText(diagnosticPath, url);
		}

		return base.OpenBrowser(url);
	}
}
