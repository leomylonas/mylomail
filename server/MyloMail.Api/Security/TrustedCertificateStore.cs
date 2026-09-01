using Microsoft.EntityFrameworkCore;
using MyloMail.Api.Domain;
using MyloMail.Api.Persistence;

namespace MyloMail.Api.Security;

public sealed class TrustedCertificateStore(MyloMailDbContext context) : ITrustedCertificateStore
{
	public IReadOnlyList<AccountTrustedCertificate> GetForAccount(Guid accountId) =>
		context.AccountTrustedCertificates.Where(c => c.AccountId == accountId).ToList();

	public async Task TrustAsync(
		Guid accountId,
		string expectedHostname,
		string sha256Fingerprint,
		CancellationToken ct
	)
	{
		var exists = await context.AccountTrustedCertificates.AnyAsync(
			c =>
				c.AccountId == accountId
				&& c.ExpectedHostname == expectedHostname
				&& c.Sha256Fingerprint == sha256Fingerprint,
			ct
		);
		if (exists)
		{
			return;
		}

		context.AccountTrustedCertificates.Add(
			new AccountTrustedCertificate
			{
				Id = Guid.NewGuid(),
				AccountId = accountId,
				ExpectedHostname = expectedHostname,
				Sha256Fingerprint = sha256Fingerprint,
			}
		);
		await context.SaveChangesAsync(ct);
	}
}
