using System.Text.Json;
using MyloMail.Api.Credentials;

namespace MyloMail.Api.Tests.Credentials;

internal sealed record ProviderTestCredential(Guid AccountId, CredentialPayload Payload);

internal static class ProviderTestCredentialCache
{
	public static bool IsConfigured(string variable) =>
		!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(variable));

	public static ProviderTestCredential Load(string variable, string expectedFormat)
	{
		var encoded = Environment.GetEnvironmentVariable(variable)
			?? throw new InvalidOperationException($"{variable} is not configured.");
		try
		{
			var envelope = JsonSerializer.Deserialize<SavedCredential>(
				Convert.FromBase64String(encoded)
			) ?? throw new InvalidOperationException($"{variable} is empty.");
			if (envelope.AccountId == Guid.Empty)
			{
				throw new InvalidOperationException($"{variable} has no account identity.");
			}
			if (!envelope.Format.Equals(expectedFormat, StringComparison.Ordinal))
			{
				throw new InvalidOperationException(
					$"{variable} contains '{envelope.Format}', expected '{expectedFormat}'."
				);
			}
			return new ProviderTestCredential(
				envelope.AccountId,
				new CredentialPayload(envelope.Format, Convert.FromBase64String(envelope.Data))
			);
		}
		catch (FormatException ex)
		{
			throw new InvalidOperationException($"{variable} is not a valid provider token cache.", ex);
		}
		catch (JsonException ex)
		{
			throw new InvalidOperationException($"{variable} is not a valid provider token cache.", ex);
		}
	}

	public static async Task SeedIfMissingAsync(
		ICredentialStore store,
		ProviderTestCredential credential,
		CancellationToken ct
	)
	{
		if (await store.RetrieveAsync(credential.AccountId, ct) is null)
		{
			await store.StoreAsync(credential.AccountId, credential.Payload, ct);
		}
	}

	private sealed record SavedCredential(
		string Variable,
		Guid AccountId,
		string Format,
		string Data
	);
}
