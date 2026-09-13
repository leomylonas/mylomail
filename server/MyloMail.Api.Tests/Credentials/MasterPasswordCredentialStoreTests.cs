using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MyloMail.Api.Credentials;
using MyloMail.Api.Persistence;
using MyloMail.Api.Tests.Persistence;
using Xunit;

namespace MyloMail.Api.Tests.Credentials;

public sealed class MasterPasswordCredentialStoreTests
{
	[Fact]
	public async Task Stores_credentials_and_unlocks_on_next_launch()
	{
		await using var database = new TestDatabase();
		await database.MigrateAsync();
		var accountId = Guid.NewGuid();
		var payload = new CredentialPayload("test", "credential-secret"u8.ToArray());

		byte[] key;
		await using (var scope = database.CreateScope())
		{
			using var store = new MasterPasswordCredentialStore(scope.ServiceProvider.GetRequiredService<MyloMailDbContext>(), []);
			await store.InitializeAsync("correct horse battery staple", CancellationToken.None);
			await store.StoreAsync(accountId, payload, CancellationToken.None);
			key = store.CreateKeyCopy();
		}

		await using (var scope = database.CreateScope())
		{
			using var store = new MasterPasswordCredentialStore(scope.ServiceProvider.GetRequiredService<MyloMailDbContext>(), key);
			var retrieved = await store.RetrieveAsync(accountId, CancellationToken.None);
			Assert.Equal(payload.Format, retrieved?.Format);
			Assert.Equal(payload.Data, retrieved?.Data);
		}
	}

	[Fact]
	public async Task Rejects_an_incorrect_master_password()
	{
		await using var database = new TestDatabase();
		await database.MigrateAsync();
		await using (var scope = database.CreateScope())
		{
			using var first = new MasterPasswordCredentialStore(scope.ServiceProvider.GetRequiredService<MyloMailDbContext>(), []);
			await first.InitializeAsync("right", CancellationToken.None);
		}
		await using (var scope = database.CreateScope())
		{
			using var second = new MasterPasswordCredentialStore(scope.ServiceProvider.GetRequiredService<MyloMailDbContext>(), []);
			await Assert.ThrowsAsync<CredentialStoreUnavailableException>(() => second.InitializeAsync("wrong", CancellationToken.None));
		}
	}
}
