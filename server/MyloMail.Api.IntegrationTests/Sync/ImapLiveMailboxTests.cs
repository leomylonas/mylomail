using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MyloMail.Api.Domain;
using MyloMail.Api.Persistence;
using MyloMail.Api.Providers;
using MyloMail.Api.Providers.Imap;
using MyloMail.Api.Sync;
using MyloMail.Api.Tests.Conformance;
using MyloMail.Api.Tests.Persistence;
using Xunit;

namespace MyloMail.Api.Tests.Sync;

/// <summary>
/// Folder lifecycle against a real IMAP server (§2).
/// </summary>
/// <remarks>
/// The fake provider names folders the way we told it to, so it cannot show that a server
/// answers a create with a name of its own choosing, prefixes it with a personal namespace,
/// or uses a delimiter other than "/". Every one of those decides whether the local tree is
/// true afterwards, and none of them is observable against a fake.
/// </remarks>
[Trait("Category", "Conformance")]
[Trait("Category", "Deep")]
public sealed class ImapLiveMailboxTests
{
	private static string? Host => Environment.GetEnvironmentVariable("TEST_IMAP_QRESYNC_HOST");

	private static string? Port => Environment.GetEnvironmentVariable("TEST_IMAP_QRESYNC_PORT");

	[SkippableFact]
	public async Task A_folder_is_created_renamed_and_deleted_on_a_real_server()
	{
		Skip.If(
			string.IsNullOrWhiteSpace(Host) || string.IsNullOrWhiteSpace(Port),
			"TEST_IMAP_QRESYNC_HOST/PORT not set — start the matrix with `pnpm imap:up`"
		);

		await using var harness = await ImapConformanceHarness.CreateAsync(
			new ImapConnectionSettings(
				Host!,
				int.Parse(Port!),
				ImapSecurity: MailTransportSecurity.StartTls,
				UserName: Environment.GetEnvironmentVariable("TEST_IMAP_USER") ?? "test@mylomail.local",
				Password: Environment.GetEnvironmentVariable("TEST_IMAP_PASSWORD") ?? "password",
				CertificateTrustMode: CertificateTrustMode.TrustAll
			)
		);
		await using var database = new TestDatabase();
		await database.MigrateAsync();

		// The provider resolves mailbox ids from the database, as it does in the app, rather
		// than from the harness's fixed map: a folder created during the test is unknown to
		// any map written in advance, and resolving from the database is the behaviour §2
		// relies on for a rename to leave queued work valid.
		var services = new ServiceCollection()
			.AddLogging()
			.AddPersistence(database.Directory)
			.AddSingleton<IMailProviderFactory>(serviceProvider => new SingleProviderFactory(
				new ImapMailProvider(
					harness.Settings,
					new ScopedMailboxResolver(serviceProvider)
				)
			))
			.AddMutations()
			.AddSync()
			.BuildServiceProvider();
		await using var _ = services;

		var accountId = harness.Account.Id;
		await using (var scope = services.CreateAsyncScope())
		{
			var context = scope.ServiceProvider.GetRequiredService<MyloMailDbContext>();
			context.Accounts.Add(
				new Account
				{
					Id = accountId,
					DisplayName = harness.Account.DisplayName,
					ProviderType = ProviderType.Imap,
					InitialSyncMode = InitialSyncMode.Full,
				}
			);
			await context.SaveChangesAsync();
			await scope.ServiceProvider.GetRequiredService<TopologySyncService>()
				.ReconcileAsync(await context.Accounts.SingleAsync());
		}

		var name = $"Receipts {Guid.NewGuid():N}";
		var renamed = $"{name} kept";

		await using (var scope = services.CreateAsyncScope())
		{
			var context = scope.ServiceProvider.GetRequiredService<MyloMailDbContext>();
			await scope.ServiceProvider.GetRequiredService<MailboxManagement>()
				.CreateAsync(accountId, name, null);

			// Found by the id the server gave it, not by the name asked for: the server may
			// place it under a personal namespace, and on the CondStore tier it does.
			var all = await context.Mailboxes.ToListAsync();
			var created = Assert.Single(
				all,
				m => m.ProviderMailboxId is not null && m.ProviderMailboxId.EndsWith(name)
			);
			Assert.Equal(name, created.Name);
		}

		await using (var scope = services.CreateAsyncScope())
		{
			var context = scope.ServiceProvider.GetRequiredService<MyloMailDbContext>();
			var created = (await context.Mailboxes.ToListAsync()).Single(m =>
				m.ProviderMailboxId is not null && m.ProviderMailboxId.EndsWith(name)
			);

			await scope.ServiceProvider.GetRequiredService<MailboxManagement>()
				.RenameAsync(created.Id, renamed);

			// The local identity survives the rename, which is what lets queued work that
			// refers to this mailbox stay valid (§6).
			var after = await context.Mailboxes.SingleAsync(m => m.Id == created.Id);
			Assert.Equal(renamed, after.Name);
			Assert.EndsWith(renamed, after.ProviderMailboxId);
		}

		await using (var scope = services.CreateAsyncScope())
		{
			var context = scope.ServiceProvider.GetRequiredService<MyloMailDbContext>();
			var created = (await context.Mailboxes.ToListAsync()).Single(m =>
				m.ProviderMailboxId is not null && m.ProviderMailboxId.EndsWith(renamed)
			);

			var messagesDeletedToo = await scope.ServiceProvider
				.GetRequiredService<MailboxManagement>()
				.DeleteAsync(created.Id);

			Assert.True(messagesDeletedToo, "Deleting an IMAP folder deletes the mail in it.");
			Assert.DoesNotContain(await context.Mailboxes.ToListAsync(), m => m.Name == renamed);
		}
	}

	private sealed class SingleProviderFactory(IMailProvider provider) : IMailProviderFactory
	{
		public IMailProvider For(Account account) => provider;
	}

	/// <summary>
	/// Resolves through a fresh scope, because the provider outlives any one of them and the
	/// mapping it needs is whatever the database holds at the moment of the call.
	/// </summary>
	private sealed class ScopedMailboxResolver(IServiceProvider services) : IProviderMailboxResolver
	{
		public string ProviderMailboxId(Guid mailboxId)
		{
			using var scope = services.CreateScope();
			return new DbProviderMailboxResolver(
				scope.ServiceProvider.GetRequiredService<MyloMailDbContext>()
			).ProviderMailboxId(mailboxId);
		}

		public Guid? SpecialMailboxId(Guid accountId, SpecialUse specialUse)
		{
			using var scope = services.CreateScope();
			return new DbProviderMailboxResolver(
				scope.ServiceProvider.GetRequiredService<MyloMailDbContext>()
			).SpecialMailboxId(accountId, specialUse);
		}

		public string LocalPath(Guid mailboxId, char separator)
		{
			using var scope = services.CreateScope();
			return new DbProviderMailboxResolver(
				scope.ServiceProvider.GetRequiredService<MyloMailDbContext>()
			).LocalPath(mailboxId, separator);
		}
	}
}
