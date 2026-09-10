using System.Security.Cryptography;
using System.Text;
using MimeKit;
using MimeKit.Cryptography;
using MyloMail.Api.Security;
using Xunit;

namespace MyloMail.Api.Tests.Security;

public sealed class IncomingMailAuthenticationTests
{
	[Fact]
	public async Task A_valid_DKIM_signature_with_an_exact_DMARC_aligned_From_domain_is_authenticated()
	{
		using var rsa = RSA.Create(2048);
		var message = new MimeMessage();
		message.From.Add(MailboxAddress.Parse("bob@example.test"));
		message.To.Add(MailboxAddress.Parse("organizer@example.test"));
		message.Subject = "RSVP";
		message.Date = DateTimeOffset.UtcNow;
		message.Body = new TextPart("plain") { Text = "Accepted" };
		message.Prepare(EncodingConstraint.SevenBit);

		using var key = new MemoryStream(Encoding.ASCII.GetBytes(rsa.ExportPkcs8PrivateKeyPem()));
		var signer = new DkimSigner(key, "example.test", "selector", DkimSignatureAlgorithm.RsaSha256);
		signer.Sign(message, [HeaderId.From, HeaderId.To, HeaderId.Subject, HeaderId.Date]);

		var dns = new StaticDns(
			new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
			{
				["selector._domainkey.example.test"] =
				[
					$"v=DKIM1; k=rsa; p={Convert.ToBase64String(rsa.ExportSubjectPublicKeyInfo())}",
				],
				["_dmarc.example.test"] = ["v=DMARC1; p=reject"],
			}
		);

		var result = await new DkimDmarcAuthentication(dns).VerifyAsync(message, default);

		Assert.True(result.IsAuthenticated);
		Assert.Equal("bob@example.test", result.AuthenticatedAddress);
	}

	[Fact]
	public async Task A_valid_DKIM_signature_without_a_DMARc_policy_is_not_authenticated()
	{
		using var rsa = RSA.Create(2048);
		var message = new MimeMessage();
		message.From.Add(MailboxAddress.Parse("bob@example.test"));
		message.To.Add(MailboxAddress.Parse("organizer@example.test"));
		message.Subject = "RSVP";
		message.Date = DateTimeOffset.UtcNow;
		message.Body = new TextPart("plain") { Text = "Accepted" };
		message.Prepare(EncodingConstraint.SevenBit);

		using var key = new MemoryStream(Encoding.ASCII.GetBytes(rsa.ExportPkcs8PrivateKeyPem()));
		var signer = new DkimSigner(key, "example.test", "selector", DkimSignatureAlgorithm.RsaSha256);
		signer.Sign(message, [HeaderId.From, HeaderId.To, HeaderId.Subject, HeaderId.Date]);

		var dns = new StaticDns(
			new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
			{
				["selector._domainkey.example.test"] =
				[
					$"v=DKIM1; k=rsa; p={Convert.ToBase64String(rsa.ExportSubjectPublicKeyInfo())}",
				],
			}
		);

		var result = await new DkimDmarcAuthentication(dns).VerifyAsync(message, default);

		Assert.False(result.IsAuthenticated);
	}

	private sealed class StaticDns(IReadOnlyDictionary<string, IReadOnlyList<string>> records) : IEmailAuthenticationDns
	{
		public Task<IReadOnlyList<string>> QueryTxtAsync(string domain, CancellationToken ct) =>
			Task.FromResult(records.GetValueOrDefault(domain, []));
	}
}
