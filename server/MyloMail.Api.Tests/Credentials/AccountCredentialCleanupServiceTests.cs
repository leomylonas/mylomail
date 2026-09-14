using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MyloMail.Api.Content;
using MyloMail.Api.Credentials;
using MyloMail.Api.Domain;
using MyloMail.Api.Persistence;
using MyloMail.Api.Tests.Persistence;
using Xunit;

namespace MyloMail.Api.Tests.Credentials;

public sealed class AccountCredentialCleanupServiceTests
{
	[Fact]
	public async Task A_partial_store_failure_leaves_durable_cleanup_that_a_retry_finishes_for_every_slot()
	{
		await using var database = new TestDatabase();
		await database.MigrateAsync();
		var accountId = Guid.NewGuid();
		var store = new FailingCredentialStore { FailOnDeleteAttempt = 3 };
		foreach (var slot in new[]
		{
			CredentialSlots.Primary,
			CredentialSlots.CalDav,
			CredentialSlots.Smtp,
			CredentialSlots.GmailClientSecret,
		})
		{
			await store.StoreAsync(
				CredentialSlots.Key(accountId, slot),
				new CredentialPayload("test", [1]),
				CancellationToken.None
			);
		}

		await using (var scope = database.CreateScope())
		{
			var context = scope.ServiceProvider.GetRequiredService<MyloMailDbContext>();
			context.Accounts.Add(new Account
			{
				Id = accountId,
				DisplayName = "Removing",
				ProviderType = ProviderType.Imap,
				AuthState = AuthState.Connected,
				IsEnabled = false,
			});
			context.AccountCredentialCleanups.Add(new AccountCredentialCleanup { AccountId = accountId });
			await context.SaveChangesAsync();

			var cleanup = new AccountCredentialCleanupService(
				context,
				store,
				new SearchIndexer(context),
				NullLogger<AccountCredentialCleanupService>.Instance
			);
			await Assert.ThrowsAsync<InvalidOperationException>(() => cleanup.CompleteAsync(accountId));
			Assert.True(await context.AccountCredentialCleanups.AnyAsync(c => c.AccountId == accountId));
			Assert.False(await context.Accounts.AnyAsync(account => account.Id == accountId));
		}

		await using (var scope = database.CreateScope())
		{
			var context = scope.ServiceProvider.GetRequiredService<MyloMailDbContext>();
			var cleanup = new AccountCredentialCleanupService(
				context,
				store,
				new SearchIndexer(context),
				NullLogger<AccountCredentialCleanupService>.Instance
			);
			await cleanup.CompletePendingAsync();

			Assert.False(await context.AccountCredentialCleanups.AnyAsync(c => c.AccountId == accountId));
			foreach (var slot in new[]
			{
				CredentialSlots.Primary,
				CredentialSlots.CalDav,
				CredentialSlots.Smtp,
				CredentialSlots.GmailClientSecret,
			})
			{
				Assert.Null(await store.RetrieveAsync(CredentialSlots.Key(accountId, slot), default));
			}
		}
	}

	private sealed class FailingCredentialStore : ICredentialStore
	{
		private readonly Dictionary<Guid, CredentialPayload> credentials = [];
		private int deleteAttempts;
		public int FailOnDeleteAttempt { get; set; }

		public Task StoreAsync(Guid accountId, CredentialPayload payload, CancellationToken ct)
		{
			credentials[accountId] = payload;
			return Task.CompletedTask;
		}

		public Task<CredentialPayload?> RetrieveAsync(Guid accountId, CancellationToken ct) =>
			Task.FromResult(credentials.GetValueOrDefault(accountId));

		public Task DeleteAsync(Guid accountId, CancellationToken ct)
		{
			if (++deleteAttempts == FailOnDeleteAttempt)
			{
				FailOnDeleteAttempt = 0;
				throw new InvalidOperationException("Credential store unavailable.");
			}
			credentials.Remove(accountId);
			return Task.CompletedTask;
		}
	}
}
