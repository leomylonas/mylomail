using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MyloMail.Api.Compose;
using MyloMail.Api.Domain;
using MyloMail.Api.Persistence;
using MyloMail.Api.Tests.Persistence;
using Xunit;

namespace MyloMail.Api.Tests.Compose;

/// <summary>
/// Send-as identity management (§1, §15): a real alias scenario needs more than one identity
/// per account, each addable, editable, deletable and promotable to default.
/// </summary>
public sealed class SendIdentityServiceTests
{
	[Fact]
	public async Task The_first_identity_added_to_an_account_becomes_its_default()
	{
		await using var database = new TestDatabase();
		await database.MigrateAsync();
		var accountId = await SeedAccountAsync(database);

		var identity = await UsingAsync(database, context =>
			new SendIdentityService(context).AddAsync(accountId, "Work", "work@example.com", null)
		);

		Assert.True(identity.IsDefault);
	}

	[Fact]
	public async Task A_second_identity_is_not_the_default()
	{
		await using var database = new TestDatabase();
		await database.MigrateAsync();
		var accountId = await SeedAccountAsync(database);
		await UsingAsync(database, context =>
			new SendIdentityService(context).AddAsync(accountId, "Work", "work@example.com", null)
		);

		var second = await UsingAsync(database, context =>
			new SendIdentityService(context).AddAsync(accountId, "Alias", "alias@example.com", null)
		);

		Assert.False(second.IsDefault);
	}

	[Fact]
	public async Task Setting_a_new_default_demotes_the_old_one()
	{
		await using var database = new TestDatabase();
		await database.MigrateAsync();
		var accountId = await SeedAccountAsync(database);
		var first = await UsingAsync(database, context =>
			new SendIdentityService(context).AddAsync(accountId, "Work", "work@example.com", null)
		);
		var second = await UsingAsync(database, context =>
			new SendIdentityService(context).AddAsync(accountId, "Alias", "alias@example.com", null)
		);

		await UsingAsync(database, context => new SendIdentityService(context).SetDefaultAsync(second.Id));

		await UsingAsync(database, async context =>
		{
			Assert.False((await context.SendIdentities.SingleAsync(i => i.Id == first.Id)).IsDefault);
			Assert.True((await context.SendIdentities.SingleAsync(i => i.Id == second.Id)).IsDefault);
			return true;
		});
	}

	[Fact]
	public async Task The_default_identity_cannot_be_deleted()
	{
		await using var database = new TestDatabase();
		await database.MigrateAsync();
		var accountId = await SeedAccountAsync(database);
		var identity = await UsingAsync(database, context =>
			new SendIdentityService(context).AddAsync(accountId, "Work", "work@example.com", null)
		);

		await Assert.ThrowsAsync<InvalidOperationException>(() =>
			UsingAsync(database, context => new SendIdentityService(context).DeleteAsync(identity.Id))
		);
	}

	[Fact]
	public async Task An_identity_referenced_by_a_saved_draft_cannot_be_deleted()
	{
		await using var database = new TestDatabase();
		await database.MigrateAsync();
		var accountId = await SeedAccountAsync(database);
		var first = await UsingAsync(database, context =>
			new SendIdentityService(context).AddAsync(accountId, "Work", "work@example.com", null)
		);
		var second = await UsingAsync(database, context =>
			new SendIdentityService(context).AddAsync(accountId, "Alias", "alias@example.com", null)
		);
		await UsingAsync(database, async context =>
		{
			context.Drafts.Add(
				new Draft
				{
					Id = Guid.NewGuid(),
					AccountId = accountId,
					SendIdentityId = second.Id,
				}
			);
			await context.SaveChangesAsync();
			return true;
		});

		await Assert.ThrowsAsync<InvalidOperationException>(() =>
			UsingAsync(database, context => new SendIdentityService(context).DeleteAsync(second.Id))
		);

		// The other (unreferenced, non-default) identity deletes cleanly, confirming the
		// rejection above is really about the draft reference and not something broader.
		await UsingAsync(database, context => new SendIdentityService(context).SetDefaultAsync(second.Id));
		await UsingAsync(database, context => new SendIdentityService(context).DeleteAsync(first.Id));
		await UsingAsync(database, async context =>
			Assert.False(await context.SendIdentities.AnyAsync(i => i.Id == first.Id))
		);
	}


	private static async Task<T> UsingAsync<T>(TestDatabase database, Func<MyloMailDbContext, Task<T>> work)
	{
		await using var scope = database.CreateScope();
		return await work(scope.ServiceProvider.GetRequiredService<MyloMailDbContext>());
	}

	private static Task UsingAsync(TestDatabase database, Func<MyloMailDbContext, Task> work) =>
		UsingAsync(
			database,
			async context =>
			{
				await work(context);
				return true;
			}
		);

	private static async Task<Guid> SeedAccountAsync(TestDatabase database) =>
		await UsingAsync(database, async context =>
		{
			var account = new Account { Id = Guid.NewGuid(), ProviderType = ProviderType.Imap };
			context.Accounts.Add(account);
			await context.SaveChangesAsync();
			return account.Id;
		});
}
