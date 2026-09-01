using Microsoft.Extensions.DependencyInjection;
using MyloMail.Api.Persistence;
using MyloMail.Api.Providers;
using MyloMail.Api.Security;
using MyloMail.Api.Tests.Fakes;
using MyloMail.Api.Tests.Sync;
using Xunit;

namespace MyloMail.Api.Tests.Security;

public sealed class TrustedCertificateStoreTests
{
	[Fact]
	public async Task Trusting_the_same_certificate_twice_records_it_once()
	{
		await using var harness = await SyncHarness.CreateAsync(ProviderShapes.Imap(ImapCapabilityTier.QResync));

		await harness.UsingAsync(async scope =>
		{
			var store = new TrustedCertificateStore(scope.GetRequiredService<MyloMailDbContext>());

			await store.TrustAsync(harness.Account.Id, "mail.example.test", "abc123", default);
			await store.TrustAsync(harness.Account.Id, "mail.example.test", "abc123", default);

			var pinned = store.GetForAccount(harness.Account.Id);
			var only = Assert.Single(pinned);
			Assert.Equal("mail.example.test", only.ExpectedHostname);
			Assert.Equal("abc123", only.Sha256Fingerprint);
		});
	}

	[Fact]
	public async Task Different_hostnames_are_pinned_independently_for_certificate_rotation()
	{
		await using var harness = await SyncHarness.CreateAsync(ProviderShapes.Imap(ImapCapabilityTier.QResync));

		await harness.UsingAsync(async scope =>
		{
			var store = new TrustedCertificateStore(scope.GetRequiredService<MyloMailDbContext>());

			await store.TrustAsync(harness.Account.Id, "mail.example.test", "old-fingerprint", default);
			await store.TrustAsync(harness.Account.Id, "mail.example.test", "new-fingerprint", default);

			Assert.Equal(2, store.GetForAccount(harness.Account.Id).Count);
		});
	}
}
