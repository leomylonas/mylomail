using MyloMail.Api.Credentials;
using Xunit;

namespace MyloMail.Api.IntegrationTests.Credentials;

/// <summary>
/// Exercises the production credential adapter against the current host's real OS store.
/// The environment gate prevents unattended development and CI runs from opening keychain
/// prompts or writing durable user secrets.
/// </summary>
[Trait("Category", "Deep")]
public sealed class NativeCredentialStoreLiveTests
{
	[SkippableFact]
	public async Task Native_store_round_trips_replaces_and_deletes_a_credential()
	{
		Skip.If(
			Environment.GetEnvironmentVariable("MYLOMAIL_TEST_NATIVE_CREDENTIAL_STORE") != "1",
			"Set MYLOMAIL_TEST_NATIVE_CREDENTIAL_STORE=1 on a native interactive host."
		);

		var accountId = Guid.NewGuid();
		var dataDirectory = Path.Combine(
			Path.GetTempPath(),
			$"mylomail-native-credential-{Guid.NewGuid():N}"
		);
		var store = new NativeCredentialStore(dataDirectory);

		try
		{
			var initial = new CredentialPayload("native-live-v1", [0, 1, 2, 255]);
			await store.StoreAsync(accountId, initial, CancellationToken.None);
			var firstRead = await store.RetrieveAsync(accountId, CancellationToken.None);
			Assert.Equal(initial.Format, firstRead?.Format);
			Assert.Equal(initial.Data, firstRead?.Data);

			var replacement = new CredentialPayload("native-live-v2", [9, 8, 7]);
			await store.StoreAsync(accountId, replacement, CancellationToken.None);
			var secondRead = await store.RetrieveAsync(accountId, CancellationToken.None);
			Assert.Equal(replacement.Format, secondRead?.Format);
			Assert.Equal(replacement.Data, secondRead?.Data);

			await store.DeleteAsync(accountId, CancellationToken.None);
			await store.DeleteAsync(accountId, CancellationToken.None);
			Assert.Null(await store.RetrieveAsync(accountId, CancellationToken.None));
		}
		finally
		{
			try
			{
				await store.DeleteAsync(accountId, CancellationToken.None);
			}
			catch (CredentialStoreUnavailableException)
			{
				// Preserve the primary assertion if the host store becomes unavailable during cleanup.
			}
			if (Directory.Exists(dataDirectory)) Directory.Delete(dataDirectory, recursive: true);
		}
	}
}
