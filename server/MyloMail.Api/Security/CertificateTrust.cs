using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using MyloMail.Api.Domain;
using MyloMail.Api.Errors;

namespace MyloMail.Api.Security;

/// <summary>
/// The one TLS trust decision every provider's transport makes the same way (§15).
/// </summary>
/// <remarks>
/// <b>The rule, stated precisely</b> (an earlier "on top of, not instead of" phrasing was
/// self-defeating — the only reason to pin is that normal validation failed): validate
/// normally first; only when normal validation fails does a pinned fingerprint for the
/// expected hostname get a say. A legitimately-signed certificate still validates normally and
/// needs no pin. <see cref="CertificateTrustMode.TrustAll"/> bypasses both checks entirely —
/// a genuine security downgrade, which is why enabling it carries its own in-app warning.
/// </remarks>
public static class CertificateTrust
{
	public static bool Validate(
		CertificateTrustMode mode,
		IReadOnlyList<AccountTrustedCertificate> pinned,
		string hostname,
		X509Certificate2 certificate,
		SslPolicyErrors sslPolicyErrors
	)
	{
		if (mode == CertificateTrustMode.TrustAll)
		{
			return true;
		}

		if (sslPolicyErrors == SslPolicyErrors.None)
		{
			return true;
		}

		var fingerprint = Convert.ToHexString(certificate.GetCertHash(HashAlgorithmName.SHA256)).ToLowerInvariant();
		return pinned.Any(p =>
			string.Equals(p.ExpectedHostname, hostname, StringComparison.OrdinalIgnoreCase)
			&& string.Equals(p.Sha256Fingerprint, fingerprint, StringComparison.OrdinalIgnoreCase)
		);
	}

	/// <summary>Lowercase hex SHA-256, the same form pinned entries and this class both use.</summary>
	public static string Fingerprint(X509Certificate2 certificate) =>
		Convert.ToHexString(certificate.GetCertHash(HashAlgorithmName.SHA256)).ToLowerInvariant();

	/// <summary>
	/// The <see cref="ErrorCategory.Validation"/> problem every provider's transport reports a
	/// rejected certificate as. The fingerprint and hostname travel as <c>Extensions</c> rather
	/// than being left to parse out of <see cref="ProblemDetails.Detail"/>: they are exactly
	/// what <c>TrustCertificate</c> needs, and a user decision should not depend on scraping
	/// prose that exists to be read, not parsed.
	/// </summary>
	public static MutationProblemDetails Problem(string hostname, string fingerprint, string issuer)
	{
		var problem = new MutationProblemDetails
		{
			Title = "Certificate untrusted",
			Detail = $"The certificate presented by {hostname} is not trusted (issued by {issuer}, SHA-256 {fingerprint}).",
			Category = ErrorCategory.Validation,
		};
		problem.Extensions["hostname"] = hostname;
		problem.Extensions["sha256Fingerprint"] = fingerprint;
		return problem;
	}
}
