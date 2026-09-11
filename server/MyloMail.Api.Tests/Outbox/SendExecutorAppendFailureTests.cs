using MyloMail.Api.Domain;
using MyloMail.Api.Outbox;
using MyloMail.Api.Providers;
using MyloMail.Api.Providers.Imap;
using MyloMail.Api.Tests.Fakes;
using Xunit;

namespace MyloMail.Api.Tests.Outbox;

/// <summary>
/// Hundred-and-first architecture-review pass: <see cref="ImapMailProvider.AppendFailure"/> was
/// set on a genuine Sent-folder filing failure but read by nothing anywhere — not logged, not
/// surfaced, not stored. <see cref="SendExecutor.AppendFailureOf"/> is the pattern-match
/// <see cref="SendExecutor.SendAsync"/> now uses to log it. Exercising the real end-to-end path
/// needs a live IMAP/SMTP round trip that deliberately fails APPEND, so this instead drives the
/// extracted pattern-match directly against a real <see cref="ImapMailProvider"/> instance whose
/// failure is forced via a test-only seam, with no network involved.
/// </summary>
public sealed class SendExecutorAppendFailureTests
{
	[Fact]
	public void An_imap_providers_append_failure_is_surfaced()
	{
		var provider = new ImapMailProvider(
			new ImapConnectionSettings(
				"imap.example.test",
				993,
				MailTransportSecurity.TlsOnConnect,
				"user@example.test",
				"password"
			),
			new ThrowingMailboxResolver()
		);
		provider.SimulateAppendFailure("the Sent folder does not exist");

		Assert.Equal("the Sent folder does not exist", SendExecutor.AppendFailureOf(provider));
	}

	[Fact]
	public void An_imap_provider_with_no_append_failure_reports_none()
	{
		var provider = new ImapMailProvider(
			new ImapConnectionSettings(
				"imap.example.test",
				993,
				MailTransportSecurity.TlsOnConnect,
				"user@example.test",
				"password"
			),
			new ThrowingMailboxResolver()
		);

		Assert.Null(SendExecutor.AppendFailureOf(provider));
	}

	[Fact]
	public void A_non_imap_provider_never_matches_the_pattern()
	{
		Assert.Null(SendExecutor.AppendFailureOf(new FakeMailProvider(ProviderShapes.Gmail)));
	}

	private sealed class ThrowingMailboxResolver : IProviderMailboxResolver
	{
		public string ProviderMailboxId(Guid mailboxId) => throw new NotSupportedException();

		public Guid? SpecialMailboxId(Guid accountId, MyloMail.Api.Domain.SpecialUse specialUse) =>
			throw new NotSupportedException();

		public string LocalPath(Guid mailboxId, char separator) => throw new NotSupportedException();
	}
}
