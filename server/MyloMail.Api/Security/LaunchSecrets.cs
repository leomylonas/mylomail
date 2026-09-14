using System.Text.Json;

namespace MyloMail.Api.Security;

/// <summary>
/// Secrets supplied once by Electron over the owned child's private standard-input pipe.
/// They are deliberately never accepted from the process environment or command line.
/// </summary>
public sealed record LaunchSecrets(string LaunchToken, string? MasterPassword)
{
	public static LaunchSecrets ReadFromStandardInput()
	{
		using var input = Console.OpenStandardInput();
		using var document = JsonDocument.Parse(input);
		var root = document.RootElement;
		if (!root.TryGetProperty("launchToken", out var token)
			|| token.ValueKind != JsonValueKind.String
			|| string.IsNullOrWhiteSpace(token.GetString()))
		{
			throw new InvalidOperationException("Electron did not supply a launch token.");
		}

		var password = root.TryGetProperty("masterPassword", out var suppliedPassword)
			&& suppliedPassword.ValueKind == JsonValueKind.String
			? suppliedPassword.GetString()
			: null;
		return new LaunchSecrets(token.GetString()!, password);
	}
}
