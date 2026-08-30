using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MyloMail.Api.Domain;
using MyloMail.Api.Persistence;
using MyloMail.Api.Sync;
using MyloMail.Api.Tests.Fakes;
using Xunit;

namespace MyloMail.Api.Tests.Sync;

/// <summary>Topology, coverage and the change stream — §3's separate concerns.</summary>
public sealed class SyncTests
{
	[Fact]
	public async Task Topology_reconciliation_creates_mailboxes_and_refreshes_provider_counts()
	{
		await using var harness = await SyncHarness.CreateAsync(ProviderShapes.Gmail);
		var inbox = harness.Provider.AddMailbox("INBOX", SpecialUse.Inbox);
		harness.Provider.AddMailbox("ARCHIVE", SpecialUse.Archive);
		harness.Provider.SeedMessage("INBOX", Guid.NewGuid(), DateTimeOffset.UnixEpoch);

		await ReconcileAsync(harness);

		await harness.UsingAsync(async scope =>
		{
			var context = scope.GetRequiredService<MyloMailDbContext>();
			var mailboxes = await context.Mailboxes.ToListAsync();

			Assert.Equal(2, mailboxes.Count);

			// Provider-reported counts drive the sidebar; a locally computed count is wrong
			// under bounded sync.
			var stored = mailboxes.Single(m => m.ProviderMailboxId == "INBOX");
			Assert.Equal(SpecialUse.Inbox, stored.SpecialUse);
			Assert.NotNull(stored.ProviderTotalCount);
		});

		_ = inbox;
	}

	/// <summary>A mailbox the provider no longer reports is removed locally.</summary>
	[Fact]
	public async Task Topology_reconciliation_removes_a_vanished_mailbox()
	{
		await using var harness = await SyncHarness.CreateAsync(ProviderShapes.Gmail);
		harness.Provider.AddMailbox("INBOX", SpecialUse.Inbox);
		harness.Provider.AddMailbox("GOING");
		await ReconcileAsync(harness);

		harness.Provider.RemoveMailbox("GOING");
		var change = await ReconcileAsync(harness);

		Assert.Equal(1, change.Removed);
		await harness.UsingAsync(async scope =>
		{
			var context = scope.GetRequiredService<MyloMailDbContext>();
			Assert.Null(await context.Mailboxes.FirstOrDefaultAsync(m => m.ProviderMailboxId == "GOING"));
		});
	}

	[Fact]
	public async Task Coverage_backfills_and_records_progress()
	{
		await using var harness = await SyncHarness.CreateAsync(ProviderShapes.Graph);
		harness.Provider.AddMailbox("INBOX", SpecialUse.Inbox);
		for (var i = 0; i < 5; i++)
		{
			harness.Provider.SeedMessage("INBOX", Guid.NewGuid(), DateTimeOffset.UnixEpoch.AddMinutes(i));
		}

		await ReconcileAsync(harness);
		await CoverAsync(harness);

		await harness.UsingAsync(async scope =>
		{
			var context = scope.GetRequiredService<MyloMailDbContext>();
			var coverage = await context.MailboxCoverageStates.SingleAsync();

			Assert.Equal(CoverageStatus.Covered, coverage.Status);
			Assert.Equal(5, coverage.MessagesFetched);
			Assert.Equal(5, await context.Messages.CountAsync());
			Assert.Equal(5, await context.MessageMailboxes.CountAsync());
		});
	}

	/// <summary>
	/// Every write is an upsert, which is what makes replaying a page safe — and replay is the
	/// price of the rule that a page is never skipped.
	/// </summary>
	[Fact]
	public async Task Replaying_a_coverage_page_is_idempotent()
	{
		await using var harness = await SyncHarness.CreateAsync(ProviderShapes.Graph);
		harness.Provider.AddMailbox("INBOX", SpecialUse.Inbox);
		harness.Provider.SeedMessage("INBOX", Guid.NewGuid(), DateTimeOffset.UnixEpoch);

		await ReconcileAsync(harness);
		await CoverAsync(harness);

		// Force the same page to be fetched and applied a second time.
		await harness.UsingAsync(async scope =>
		{
			var context = scope.GetRequiredService<MyloMailDbContext>();
			var coverage = await context.MailboxCoverageStates.SingleAsync();
			coverage.Status = CoverageStatus.NotStarted;
			coverage.ResumeToken = null;
			await context.SaveChangesAsync();
		});
		await CoverAsync(harness);

		await harness.UsingAsync(async scope =>
		{
			var context = scope.GetRequiredService<MyloMailDbContext>();
			Assert.Equal(1, await context.Messages.CountAsync());
			Assert.Equal(1, await context.MessageMailboxes.CountAsync());
		});
	}

	/// <summary>
	/// An invalid cursor from any provider takes one triggered-resynchronisation path: the
	/// cursor is discarded and a baseline re-established, rather than each provider's failure
	/// being handled separately.
	/// </summary>
	[Fact]
	public async Task An_invalidated_cursor_triggers_resynchronisation()
	{
		await using var harness = await SyncHarness.CreateAsync(ProviderShapes.Graph);
		harness.Provider.AddMailbox("INBOX", SpecialUse.Inbox);
		await ReconcileAsync(harness);
		await CoverAsync(harness);
		await SyncAsync(harness);

		harness.Provider.InvalidateCursors();
		var outcome = await SyncAsync(harness);

		Assert.True(outcome.ResyncTriggered);

		await harness.UsingAsync(async scope =>
		{
			var context = scope.GetRequiredService<MyloMailDbContext>();
			var state = await context.ChangeStreamStates.SingleAsync();
			var coverage = await context.MailboxCoverageStates.SingleAsync();

			Assert.Null(state.CursorState);
			Assert.Null(state.BaselineEstablishedAt);
			Assert.Equal(CoverageStatus.NotStarted, coverage.Status);
			Assert.NotNull(await context.IntegrityReconciliationStates.SingleOrDefaultAsync());
		});
	}

	/// <summary>
	/// Gmail's stream is account-scoped: one row with a null mailbox, however many labels the
	/// account has. Per-label cursors would consume the same stream repeatedly and race.
	/// </summary>
	[Fact]
	public async Task Gmail_keeps_one_account_scoped_change_stream_for_every_label()
	{
		await using var harness = await SyncHarness.CreateAsync(ProviderShapes.Gmail);
		harness.Provider.AddMailbox("INBOX", SpecialUse.Inbox);
		harness.Provider.AddMailbox("RECEIPTS");
		await ReconcileAsync(harness);
		await CoverAsync(harness, "INBOX");
		await CoverAsync(harness, "RECEIPTS");

		await SyncAsync(harness, "INBOX");
		await SyncAsync(harness, "RECEIPTS");

		await harness.UsingAsync(async scope =>
		{
			var context = scope.GetRequiredService<MyloMailDbContext>();
			var state = await context.ChangeStreamStates.SingleAsync();
			Assert.Null(state.MailboxId);
		});
	}

	/// <summary>
	/// Gmail drains history durably but unapplied while backfill runs. Applying it
	/// concurrently would let a stale backfill page resurrect a membership history has already
	/// removed.
	/// </summary>
	[Fact]
	public async Task Gmail_stages_history_while_coverage_is_incomplete_and_replays_after()
	{
		await using var harness = await SyncHarness.CreateAsync(ProviderShapes.Gmail);
		harness.Provider.AddMailbox("INBOX", SpecialUse.Inbox);
		await ReconcileAsync(harness);

		// Coverage has not run, so the stream is drained into staging rather than applied.
		harness.Provider.SeedMessage("INBOX", Guid.NewGuid(), DateTimeOffset.UnixEpoch);
		var outcome = await SyncAsync(harness);

		Assert.True(outcome.Staged);
		await harness.UsingAsync(async scope =>
		{
			var context = scope.GetRequiredService<MyloMailDbContext>();
			Assert.NotEmpty(await context.StagedChangeEvents.ToListAsync());

			// Nothing has reached the canonical model yet.
			Assert.Empty(await context.Messages.ToListAsync());
		});

		await CoverAsync(harness);
		var replayed = await harness.UsingAsync(async scope =>
			await scope
				.GetRequiredService<ChangeStreamService>()
				.ReplayStagedAsync(await harness.AccountInScopeAsync(scope))
		);

		Assert.True(replayed > 0);
		await harness.UsingAsync(async scope =>
		{
			var context = scope.GetRequiredService<MyloMailDbContext>();
			Assert.Empty(await context.StagedChangeEvents.ToListAsync());
			Assert.NotEmpty(await context.Messages.ToListAsync());
		});
	}

	internal static Task<TopologyChange> ReconcileAsync(SyncHarness harness) =>
		harness.UsingAsync(async scope =>
			await scope
				.GetRequiredService<TopologySyncService>()
				.ReconcileAsync(await harness.AccountInScopeAsync(scope))
		);

	internal static Task CoverAsync(
		SyncHarness harness,
		string providerMailboxId = "INBOX",
		int pageSize = 200
	) =>
		harness.UsingAsync(async scope =>
			await scope
				.GetRequiredService<CoverageService>()
				.RunToCompletionAsync(
					await harness.AccountInScopeAsync(scope),
					await harness.MailboxAsync(scope, providerMailboxId),
					pageSize
				)
		);

	internal static Task<ChangeStreamOutcome> SyncAsync(SyncHarness harness, string providerMailboxId = "INBOX") =>
		harness.UsingAsync(async scope =>
			await scope
				.GetRequiredService<ChangeStreamService>()
				.SyncAsync(
					await harness.AccountInScopeAsync(scope),
					await harness.MailboxAsync(scope, providerMailboxId)
				)
		);
}
