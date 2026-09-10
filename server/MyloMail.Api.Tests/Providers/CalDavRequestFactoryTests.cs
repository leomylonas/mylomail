using MyloMail.Api.Credentials;
using MyloMail.Api.Domain;
using MyloMail.Api.Providers;
using MyloMail.Api.Providers.CalDav;
using Xunit;

namespace MyloMail.Api.Tests.Providers;

public sealed class CalDavRequestFactoryTests
{
	[Fact]
	public async Task Reuse_reads_the_primary_imap_credential()
	{
		var account = Account(CredentialSource.ReuseImap);
		var store = new InMemoryCredentialStore();
		await store.StoreAsync(account.Id, new CredentialPayload(MailProviderFactory.ImapPasswordFormat, "imap-secret"u8.ToArray()), default);

		using var request = await new CalDavRequestFactory(store).CreateAsync(account, HttpMethod.Get);

		Assert.Equal("Basic", request.Headers.Authorization!.Scheme);
		Assert.Equal("caldav-user:imap-secret", Decode(request));
	}

	[Fact]
	public async Task Independent_credential_reads_only_the_caldav_slot()
	{
		var account = Account(CredentialSource.Independent);
		var store = new InMemoryCredentialStore();
		await store.StoreSlotAsync(account.Id, CredentialSlots.CalDav, new CredentialPayload(CalDavRequestFactory.PasswordFormat, "caldav-secret"u8.ToArray()), default);

		using var request = await new CalDavRequestFactory(store).CreateAsync(account, HttpMethod.Get);

		Assert.Equal("caldav-user:caldav-secret", Decode(request));
	}

	[Fact]
	public async Task A_server_supplied_target_outside_the_configured_collection_is_rejected_before_credentials_are_attached()
	{
		var account = Account(CredentialSource.Independent);
		var store = new InMemoryCredentialStore();
		await store.StoreSlotAsync(
			account.Id,
			CredentialSlots.CalDav,
			new CredentialPayload(CalDavRequestFactory.PasswordFormat, "caldav-secret"u8.ToArray()),
			default
		);

		await Assert.ThrowsAsync<InvalidOperationException>(() =>
			new CalDavRequestFactory(store).CreateAsync(account, HttpMethod.Get, new Uri("https://attacker.example/leak"))
		);
	}

	private static Account Account(CredentialSource source) => new()
	{
		Id = Guid.NewGuid(),
		ProviderType = ProviderType.Imap,
		ProviderConfig = new ImapProviderConfig
		{
			CalDav = new CalDavProviderConfig
			{
				Endpoint = "https://calendar.example.test/dav/",
				UserName = "caldav-user",
				CredentialSource = source,
			},
		},
	};

	private static string Decode(HttpRequestMessage request) => System.Text.Encoding.UTF8.GetString(
		Convert.FromBase64String(request.Headers.Authorization!.Parameter!)
	);
}
