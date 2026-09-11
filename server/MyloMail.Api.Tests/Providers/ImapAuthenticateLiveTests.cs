using MyloMail.Api.Domain;
using MyloMail.Api.Errors;
using MyloMail.Api.Providers;
using MyloMail.Api.Providers.Imap;
using Xunit;

namespace MyloMail.Api.Tests.Providers;

/// <summary>
/// A wrong <c>UseSsl</c> setting is an ordinary, common account-setup mistake — not a reason
/// to crash the request that reports it (§15).
/// </summary>
[Trait("Category", "Conformance")]
[Trait("Category", "Deep")]
public sealed class ImapAuthenticateLiveTests
{
	private static string? Host => Environment.GetEnvironmentVariable("TEST_IMAP_QRESYNC_HOST");

	private static string? Port => Environment.GetEnvironmentVariable("TEST_IMAP_QRESYNC_PORT");

	[SkippableFact]
	public async Task An_ssl_handshake_against_a_plaintext_server_fails_as_a_network_problem_not_a_crash()
	{
		Skip.If(
			string.IsNullOrWhiteSpace(Host) || string.IsNullOrWhiteSpace(Port),
			"TEST_IMAP_QRESYNC_HOST/PORT not set — start the matrix with `pnpm imap:up`"
		);

		var provider = new ImapMailProvider(
			new ImapConnectionSettings(
				Host!,
				int.Parse(Port!),
				ImapSecurity: MailTransportSecurity.TlsOnConnect,
				UserName: Environment.GetEnvironmentVariable("TEST_IMAP_USER") ?? "test@mylomail.local",
				Password: Environment.GetEnvironmentVariable("TEST_IMAP_PASSWORD") ?? "password"
			),
			new ThrowingMailboxResolver()
		);

		var result = await provider.AuthenticateAsync(
			new Account { Id = Guid.NewGuid(), ProviderType = ProviderType.Imap },
			CancellationToken.None
		);

		Assert.False(result.Succeeded);
		Assert.Equal(AuthState.Error, result.State);
		Assert.Equal(ErrorCategory.Network, result.Problem?.Category);
	}

	/// <summary>
	/// A rejected password reaching a sync-path caller (not <see cref="ImapMailProvider.AuthenticateAsync"/>,
	/// which already had its own translation) must still surface as <see cref="ProviderAuthenticationException"/>,
	/// not the raw MailKit exception — that's what lets <c>SyncJobs.GuardAsync</c> recognise it and set
	/// <see cref="AuthState.NeedsReauth"/> instead of silently stopping the poll loop.
	/// </summary>
	[SkippableFact]
	public async Task A_rejected_password_surfaces_as_provider_authentication_from_a_sync_call()
	{
		Skip.If(
			string.IsNullOrWhiteSpace(Host) || string.IsNullOrWhiteSpace(Port),
			"TEST_IMAP_QRESYNC_HOST/PORT not set — start the matrix with `pnpm imap:up`"
		);

		var provider = new ImapMailProvider(
			new ImapConnectionSettings(
				Host!,
				int.Parse(Port!),
				ImapSecurity: MailTransportSecurity.StartTls,
				UserName: Environment.GetEnvironmentVariable("TEST_IMAP_USER") ?? "test@mylomail.local",
				Password: "definitely-the-wrong-password",
				CertificateTrustMode: CertificateTrustMode.TrustAll
			),
			new ThrowingMailboxResolver()
		);

		await Assert.ThrowsAsync<ProviderAuthenticationException>(
			() => provider.ListMailboxesAsync(
				new Account { Id = Guid.NewGuid(), ProviderType = ProviderType.Imap },
				CancellationToken.None
			)
		);
	}

	private sealed class ThrowingMailboxResolver : IProviderMailboxResolver
	{
		public string ProviderMailboxId(Guid mailboxId) => throw new NotSupportedException();

		public string LocalPath(Guid mailboxId, char separator) => throw new NotSupportedException();
	}
}
