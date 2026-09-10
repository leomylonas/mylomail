using DnsClient;
using DnsClient.Protocol;
using MimeKit;
using MimeKit.Cryptography;
using Org.BouncyCastle.Crypto;

namespace MyloMail.Api.Security;

/// <summary>Authenticates a received RFC 5322 author address for automatic iTIP state updates.</summary>
public interface IIncomingMailAuthentication
{
	Task<MailAuthenticationResult> VerifyAsync(MimeMessage message, CancellationToken ct);
}

/// <summary>DNS TXT lookup boundary for DKIM public keys and DMARC policies.</summary>
public interface IEmailAuthenticationDns
{
	Task<IReadOnlyList<string>> QueryTxtAsync(string domain, CancellationToken ct);
}

public sealed class EmailAuthenticationDns(IDnsQuery dns) : IEmailAuthenticationDns
{
	public async Task<IReadOnlyList<string>> QueryTxtAsync(string domain, CancellationToken ct)
	{
		var response = await dns.QueryAsync(domain, QueryType.TXT, cancellationToken: ct);
		return [.. response.Answers.TxtRecords().Select(record => string.Concat(record.Text))];
	}
}

/// <summary>
/// A reply is eligible for automatic RSVP application only when a DKIM signature validates
/// against DNS and its signing domain exactly matches a single MIME From domain with a published
/// DMARC policy. Exact alignment is deliberately stricter than DMARC's relaxed mode: it is a
/// safe subset that avoids guessing an organisational domain without a public-suffix database.
/// </summary>
public sealed class DkimDmarcAuthentication(IEmailAuthenticationDns dns) : IIncomingMailAuthentication
{
	public async Task<MailAuthenticationResult> VerifyAsync(MimeMessage message, CancellationToken ct)
	{
		var from = message.From.Mailboxes.ToList();
		if (from.Count != 1 || DomainOf(from[0].Address) is not { } fromDomain)
		{
			return MailAuthenticationResult.Unverified;
		}

		try
		{
			var verifier = new DkimVerifier(new DnsDkimPublicKeyLocator(dns));
			foreach (var signature in message.Headers.Where(header => header.Id == HeaderId.DkimSignature))
			{
				var signingDomain = SigningDomain(signature.Value);
				if (!string.Equals(signingDomain, fromDomain, StringComparison.OrdinalIgnoreCase)
					|| !await verifier.VerifyAsync(message, signature, ct))
				{
					continue;
				}

				if (await HasDmarcPolicyAsync(fromDomain, ct))
				{
					return new MailAuthenticationResult(true, from[0].Address);
				}
			}
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (Exception)
		{
			// DNS and signature failures are not a reason to reject the message. They only prevent
			// its untrusted RSVP claim from changing calendar state automatically.
		}

		return MailAuthenticationResult.Unverified;
	}

	private async Task<bool> HasDmarcPolicyAsync(string fromDomain, CancellationToken ct)
	{
		foreach (var value in await dns.QueryTxtAsync($"_dmarc.{fromDomain}", ct))
		{
			var tags = Tags(value);
			if (tags.TryGetValue("v", out var version)
				&& string.Equals(version, "DMARC1", StringComparison.OrdinalIgnoreCase)
				&& tags.TryGetValue("p", out var policy)
				&& policy is "none" or "quarantine" or "reject")
			{
				return true;
			}
		}
		return false;
	}

	private static IReadOnlyDictionary<string, string> Tags(string value) => value
		.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
		.Select(tag => tag.Split('=', 2, StringSplitOptions.TrimEntries))
		.Where(parts => parts.Length == 2)
		.ToDictionary(parts => parts[0], parts => parts[1], StringComparer.OrdinalIgnoreCase);

	private static string? SigningDomain(string value) => Tags(value).GetValueOrDefault("d");

	private static string? DomainOf(string address)
	{
		var separator = address.LastIndexOf('@');
		var domain = separator >= 0 ? address[(separator + 1)..] : null;
		return domain is { Length: > 0 } && Uri.CheckHostName(domain) is UriHostNameType.Dns
			? domain
			: null;
	}

	private sealed class DnsDkimPublicKeyLocator(IEmailAuthenticationDns dns) : DkimPublicKeyLocatorBase
	{
		public override AsymmetricKeyParameter LocatePublicKey(
			string methods,
			string domain,
			string selector,
			CancellationToken cancellationToken = default
		) => LocatePublicKeyAsync(methods, domain, selector, cancellationToken).GetAwaiter().GetResult();

		public override async Task<AsymmetricKeyParameter> LocatePublicKeyAsync(
			string methods,
			string domain,
			string selector,
			CancellationToken cancellationToken = default
		)
		{
			if (!methods.Split(':').Contains("dns/txt", StringComparer.OrdinalIgnoreCase))
			{
				throw new NotSupportedException("Only DNS TXT DKIM key lookup is supported.");
			}

			var records = await dns.QueryTxtAsync($"{selector}._domainkey.{domain}", cancellationToken);
			var record = records.SingleOrDefault(value =>
				value.StartsWith("v=DKIM1", StringComparison.OrdinalIgnoreCase)
			) ?? throw new InvalidOperationException("No DKIM public key was found.");
			return GetPublicKey(record);
		}
	}
}

public sealed record MailAuthenticationResult(bool IsAuthenticated, string? AuthenticatedAddress)
{
	public static readonly MailAuthenticationResult Unverified = new(false, null);
}
