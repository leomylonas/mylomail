using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using MyloMail.Api.Domain;
using MyloMail.Api.Security;
using Xunit;

namespace MyloMail.Api.Tests.Security;

/// <summary>
/// The one TLS trust decision every provider's transport shares (§15).
/// </summary>
public sealed class CertificateTrustTests
{
	[Fact]
	public void A_certificate_that_validates_normally_is_trusted_regardless_of_pins()
	{
		using var certificate = SelfSigned();

		Assert.True(
			CertificateTrust.Validate(CertificateTrustMode.Default, [], "mail.example.test", certificate, SslPolicyErrors.None)
		);
	}

	[Fact]
	public void A_failed_validation_with_no_matching_pin_is_rejected()
	{
		using var certificate = SelfSigned();

		Assert.False(
			CertificateTrust.Validate(
				CertificateTrustMode.Default,
				[],
				"mail.example.test",
				certificate,
				SslPolicyErrors.RemoteCertificateChainErrors
			)
		);
	}

	[Fact]
	public void A_failed_validation_matching_a_pinned_fingerprint_and_hostname_is_trusted()
	{
		using var certificate = SelfSigned();
		var pinned = new AccountTrustedCertificate
		{
			Id = Guid.NewGuid(),
			AccountId = Guid.NewGuid(),
			ExpectedHostname = "mail.example.test",
			Sha256Fingerprint = CertificateTrust.Fingerprint(certificate),
		};

		Assert.True(
			CertificateTrust.Validate(
				CertificateTrustMode.Default,
				[pinned],
				"mail.example.test",
				certificate,
				SslPolicyErrors.RemoteCertificateChainErrors
			)
		);
	}

	[Fact]
	public void A_pin_for_a_different_hostname_does_not_vouch_for_this_one()
	{
		using var certificate = SelfSigned();
		var pinned = new AccountTrustedCertificate
		{
			Id = Guid.NewGuid(),
			AccountId = Guid.NewGuid(),
			ExpectedHostname = "other.example.test",
			Sha256Fingerprint = CertificateTrust.Fingerprint(certificate),
		};

		Assert.False(
			CertificateTrust.Validate(
				CertificateTrustMode.Default,
				[pinned],
				"mail.example.test",
				certificate,
				SslPolicyErrors.RemoteCertificateChainErrors
			)
		);
	}

	[Fact]
	public void TrustAll_bypasses_validation_even_with_no_pins_at_all()
	{
		using var certificate = SelfSigned();

		Assert.True(
			CertificateTrust.Validate(
				CertificateTrustMode.TrustAll,
				[],
				"mail.example.test",
				certificate,
				SslPolicyErrors.RemoteCertificateNameMismatch
			)
		);
	}

	private static X509Certificate2 SelfSigned()
	{
		using var rsa = RSA.Create(2048);
		var request = new CertificateRequest("CN=mail.example.test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
		return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
	}
}
