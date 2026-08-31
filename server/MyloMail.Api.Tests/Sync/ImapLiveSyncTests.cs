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
/// The sync engine against a real IMAP server, rather than against
/// <c>FakeMailProvider</c>.
/// </summary>
/// <remarks>
/// Everything else in this suite verifies the engine against a fake whose behaviour we wrote.
/// That is necessary and not sufficient: it cannot discover that a real server names folders
/// differently, reports counts we did not expect, or returns UIDs the ingest path mishandles.
/// This drives topology, coverage and the change stream end to end against Dovecot, which
/// nothing else does.
/// </remarks>
[Trait("Category", "Conformance")]
[Trait("Category", "Deep")]
public sealed class ImapLiveSyncTests
{
	private static string? Host => Environment.GetEnvironmentVariable("TEST_IMAP_QRESYNC_HOST");

	private static string? Port => Environment.GetEnvironmentVariable("TEST_IMAP_QRESYNC_PORT");

	[SkippableFact]
	public async Task Topology_coverage_and_change_stream_run_against_a_real_server()
	{
		Skip.If(
			string.IsNullOrWhiteSpace(Host) || string.IsNullOrWhiteSpace(Port),
			"TEST_IMAP_QRESYNC_HOST/PORT not set — start the matrix with `pnpm imap:up`"
		);

		await using var harness = await ImapConformanceHarness.CreateAsync(
			new ImapConnectionSettings(
				Host!,
				int.Parse(Port!),
				UseSsl: false,
				Environment.GetEnvironmentVariable("TEST_IMAP_USER") ?? "test@mylomail.local",
				Environment.GetEnvironmentVariable("TEST_IMAP_PASSWORD") ?? "password"
			)
		);
		await using var database = new TestDatabase();
		await database.MigrateAsync();

		var services = new ServiceCollection()
			.AddLogging()
			.AddPersistence(database.Directory)
			.AddSingleton<IMailProviderFactory>(new SingleProviderFactory(harness.Provider))
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
		}

		// Seed a message the way another client would, then discover everything from scratch.
		await harness.SeedMessageAsync(harness.Source);

		// 1 — topology. The folder names come from Dovecot, not from us.
		await using (var scope = services.CreateAsyncScope())
		{
			var context = scope.ServiceProvider.GetRequiredService<MyloMailDbContext>();
			var account = await context.Accounts.SingleAsync();
			var change = await scope.ServiceProvider.GetRequiredService<TopologySyncService>().ReconcileAsync(account);

			Assert.True(change.Added > 0, "The server reported no mailboxes at all.");
			Assert.Contains(await context.Mailboxes.ToListAsync(), m => m.SpecialUse == SpecialUse.Inbox);
		}

		// 2 — coverage. Real UIDs, through the real ingest path.
		await using (var scope = services.CreateAsyncScope())
		{
			var context = scope.ServiceProvider.GetRequiredService<MyloMailDbContext>();
			var account = await context.Accounts.SingleAsync();
			var inbox = await context.Mailboxes.FirstAsync(m => m.SpecialUse == SpecialUse.Inbox);

			await scope.ServiceProvider.GetRequiredService<CoverageService>().RunToCompletionAsync(account, inbox);

			var coverage = await context.MailboxCoverageStates.FirstAsync(c => c.MailboxId == inbox.Id);
			Assert.Equal(CoverageStatus.Covered, coverage.Status);
			Assert.NotEmpty(await context.Messages.ToListAsync());
			Assert.NotEmpty(await context.MessageMailboxes.ToListAsync());
		}

		// 3 — the change stream, and a cursor that is a real UIDVALIDITY/UID pair.
		await using (var scope = services.CreateAsyncScope())
		{
			var context = scope.ServiceProvider.GetRequiredService<MyloMailDbContext>();
			var account = await context.Accounts.SingleAsync();
			var inbox = await context.Mailboxes.FirstAsync(m => m.SpecialUse == SpecialUse.Inbox);

			var outcome = await scope.ServiceProvider.GetRequiredService<ChangeStreamService>().SyncAsync(account, inbox);

			Assert.False(outcome.ResyncTriggered);
			Assert.False(outcome.Staged, "IMAP streams are mailbox-scoped and must never be staged.");

			var state = await context.ChangeStreamStates.SingleAsync();
			Assert.Equal(inbox.Id, state.MailboxId);
			var cursor = Assert.IsType<Api.Providers.Contracts.ImapUidCursor>(state.CursorState);
			Assert.True(cursor.UidValidity > 0, "The server reported no UIDVALIDITY.");
		}
	}

	private sealed class SingleProviderFactory(IMailProvider provider) : IMailProviderFactory
	{
		public IMailProvider For(Account account) => provider;
	}
}
