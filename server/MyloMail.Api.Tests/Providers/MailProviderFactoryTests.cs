using Microsoft.Extensions.Options;
using MyloMail.Api.Credentials;
using MyloMail.Api.Domain;
using MyloMail.Api.Providers;
using MyloMail.Api.Providers.Imap;
using Xunit;

namespace MyloMail.Api.Tests.Providers;

public sealed class MailProviderFactoryTests
{
	/// <summary>
	/// A missing client registration is not an authentication failure. Nothing was attempted,
	/// so the account must not be pushed to <see cref="AuthState.NeedsReauth"/> — no amount of
	/// reauthenticating supplies a client id.
	/// </summary>
	[Theory]
	[InlineData(ProviderType.Gmail)]
	[InlineData(ProviderType.Microsoft365)]
	public void An_unconfigured_provider_reports_configuration_not_auth(ProviderType type)
	{
		var factory = Create(new ProviderClientOptions());

		var failure = Assert.Throws<ProviderNotConfiguredException>(() =>
			factory.For(new Account { Id = Guid.NewGuid(), ProviderType = type })
		);
		Assert.Equal(type, failure.Provider);
	}

	[Fact]
	public void A_configured_gmail_account_produces_a_gmail_provider()
	{
		var factory = Create(
			new ProviderClientOptions
			{
				Gmail = { ClientId = "client", ClientSecret = "secret" },
			}
		);

		var provider = factory.For(new Account { Id = Guid.NewGuid(), ProviderType = ProviderType.Gmail });

		Assert.Equal(ProviderType.Gmail, provider.Type);
	}

	/// <summary>
	/// IMAP is resolved per account: the password comes from the credential store at the moment
	/// of use, never from the <see cref="Account"/> (§4).
	/// </summary>
	[Fact]
	public async Task An_imap_account_takes_its_password_from_the_credential_store()
	{
		var accountId = Guid.NewGuid();
		var store = new InMemoryCredentialStore();
		await store.StoreAsync(
			accountId,
			new CredentialPayload(MailProviderFactory.ImapPasswordFormat, "hunter2"u8.ToArray()),
			CancellationToken.None
		);

		var provider = Create(new ProviderClientOptions(), store)
			.For(
				new Account
				{
					Id = accountId,
					ProviderType = ProviderType.Imap,
					ProviderConfig = new ImapProviderConfig
					{
						Host = "imap.example.org",
						Port = 993,
						UserName = "someone",
					},
				}
			);

		Assert.IsType<ImapMailProvider>(provider);
	}

	/// <summary>
	/// Without a stored password the account is unconfigured, not attempted. Trying an empty
	/// password looks to the server like a failed login and can count against the account's
	/// attempt limit.
	/// </summary>
	[Fact]
	public void An_imap_account_without_a_stored_password_is_unconfigured()
	{
		var failure = Assert.Throws<ProviderNotConfiguredException>(() =>
			Create(new ProviderClientOptions())
				.For(
					new Account
					{
						Id = Guid.NewGuid(),
						ProviderType = ProviderType.Imap,
						ProviderConfig = new ImapProviderConfig { Host = "imap.example.org", Port = 993 },
					}
				)
		);

		Assert.Equal(ProviderType.Imap, failure.Provider);
	}

	private static MailProviderFactory Create(ProviderClientOptions options, ICredentialStore? store = null) =>
		new(Options.Create(options), store ?? new InMemoryCredentialStore(), new StubResolver());

	private sealed class StubResolver : IProviderMailboxResolver
	{
		public string ProviderMailboxId(Guid mailboxId) => mailboxId.ToString();
	}
}
