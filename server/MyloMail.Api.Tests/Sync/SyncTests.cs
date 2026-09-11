using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MimeKit;
using MyloMail.Api.Domain;
using MyloMail.Api.FaultInjection;
using MyloMail.Api.Persistence;
using MyloMail.Api.Providers;
using MyloMail.Api.Providers.Contracts;
using MyloMail.Api.Sync;
using MyloMail.Api.Tests.Fakes;
using Xunit;

namespace MyloMail.Api.Tests.Sync;

/// <summary>Topology, coverage and the change stream — §3's separate concerns.</summary>
public sealed class SyncTests
{
	/// <summary>
	/// A Drafts observation is captured as raw MIME before the coverage page commits and
	/// materialised as one structured draft, never as a normal message (§1, §3).
	/// </summary>
	[Fact]
	public async Task Coverage_materialises_a_remote_draft_without_creating_a_message()
	{
		await using var harness = await SyncHarness.CreateAsync(ProviderShapes.Imap(ImapCapabilityTier.QResync));
		var drafts = harness.Provider.AddMailbox("Drafts", SpecialUse.Drafts);
		var occurrence = harness.Provider.SeedMessage("Drafts", Guid.NewGuid(), DateTimeOffset.UnixEpoch);
		drafts.Messages[occurrence].RawBytes = MimeBytes();
		await ReconcileAsync(harness);

		await harness.UsingAsync(async scope =>
		{
			var context = scope.GetRequiredService<MyloMailDbContext>();
			context.SendIdentities.Add(
				new SendIdentity
				{
					Id = Guid.NewGuid(),
					AccountId = harness.Account.Id,
					EmailAddress = "author@example.test",
					IsDefault = true,
				}
			);
			await context.SaveChangesAsync();
		});

		await CoverAsync(harness, "Drafts");

		await harness.UsingAsync(async scope =>
		{
			var context = scope.GetRequiredService<MyloMailDbContext>();
			var draft = await context.Drafts.SingleAsync();
			Assert.Equal("Remote draft", draft.Subject);
			Assert.Equal("<p>Body</p>", draft.BodyHtml.Trim());
			Assert.Equal(occurrence, draft.ProviderDraftId);
			Assert.Equal(occurrence, draft.ProviderRevision);
			Assert.Empty(await context.Messages.ToListAsync());
			Assert.Equal([draft.Id], harness.Events.Drafts);
		});
	}

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
	public async Task Gmail_topology_derives_stable_synthetic_hierarchy_from_flat_labels()
	{
		await using var harness = await SyncHarness.CreateAsync(ProviderShapes.Gmail);
		harness.Provider.AddMailbox("Projects/Client/Invoices");
		harness.Provider.AddMailbox("Projects/Personal");

		await ReconcileAsync(harness);
		var initialSyntheticIds = await harness.UsingAsync(async scope =>
		{
			var mailboxes = await scope.GetRequiredService<MyloMailDbContext>()
				.Mailboxes
				.ToListAsync();
			var projects = mailboxes.Single(mailbox =>
				mailbox.ProviderMailboxId is null && mailbox.Name == "Projects"
			);
			var client = mailboxes.Single(mailbox =>
				mailbox.ProviderMailboxId is null && mailbox.Name == "Client"
			);
			var invoices = mailboxes.Single(mailbox =>
				mailbox.ProviderMailboxId == "Projects/Client/Invoices"
			);
			var personal = mailboxes.Single(mailbox =>
				mailbox.ProviderMailboxId == "Projects/Personal"
			);

			Assert.Null(projects.ParentId);
			Assert.Equal(projects.Id, client.ParentId);
			Assert.Equal(client.Id, invoices.ParentId);
			Assert.Equal("Invoices", invoices.Name);
			Assert.Equal(projects.Id, personal.ParentId);
			Assert.Equal("Personal", personal.Name);
			return new[] { projects.Id, client.Id };
		});

		await ReconcileAsync(harness);
		await harness.UsingAsync(async scope =>
		{
			var syntheticIds = await scope.GetRequiredService<MyloMailDbContext>()
				.Mailboxes
				.Where(mailbox => mailbox.ProviderMailboxId == null)
				.Select(mailbox => mailbox.Id)
				.OrderBy(id => id)
				.ToListAsync();
			Assert.Equal(initialSyntheticIds.OrderBy(id => id), syntheticIds);
		});

		harness.Provider.RemoveMailbox("Projects/Client/Invoices");
		harness.Provider.RemoveMailbox("Projects/Personal");
		harness.Provider.AddMailbox("Archive/2025");
		await ReconcileAsync(harness);
		await harness.UsingAsync(async scope =>
		{
			var mailboxes = await scope.GetRequiredService<MyloMailDbContext>()
				.Mailboxes
				.ToListAsync();
			var synthetic = Assert.Single(mailboxes, mailbox => mailbox.ProviderMailboxId is null);
			Assert.Equal("Archive", synthetic.Name);
			Assert.DoesNotContain(mailboxes, mailbox => initialSyntheticIds.Contains(mailbox.Id));
		});
	}

	[Fact]
	public async Task Gmail_topology_uses_real_labels_as_existing_path_segments()
	{
		await using var harness = await SyncHarness.CreateAsync(ProviderShapes.Gmail);
		harness.Provider.AddMailbox("Projects");
		harness.Provider.AddMailbox("Projects/Client");
		harness.Provider.AddMailbox("Projects/Client/Invoices");

		await ReconcileAsync(harness);

		await harness.UsingAsync(async scope =>
		{
			var mailboxes = await scope.GetRequiredService<MyloMailDbContext>()
				.Mailboxes
				.ToListAsync();
			var projects = mailboxes.Single(mailbox => mailbox.ProviderMailboxId == "Projects");
			var client = mailboxes.Single(mailbox => mailbox.ProviderMailboxId == "Projects/Client");
			var invoices = mailboxes.Single(mailbox =>
				mailbox.ProviderMailboxId == "Projects/Client/Invoices"
			);

			Assert.Equal(3, mailboxes.Count);
			Assert.DoesNotContain(mailboxes, mailbox => mailbox.ProviderMailboxId is null);
			Assert.Equal("Projects", projects.Name);
			Assert.Null(projects.ParentId);
			Assert.Equal("Client", client.Name);
			Assert.Equal(projects.Id, client.ParentId);
			Assert.Equal("Invoices", invoices.Name);
			Assert.Equal(client.Id, invoices.ParentId);
		});
	}

	[Fact]
	public async Task Gmail_topology_replaces_a_vanished_real_intermediate_with_a_synthetic_one()
	{
		await using var harness = await SyncHarness.CreateAsync(ProviderShapes.Gmail);
		harness.Provider.AddMailbox("Projects");
		harness.Provider.AddMailbox("Projects/Client");
		harness.Provider.AddMailbox("Projects/Client/Invoices");
		await ReconcileAsync(harness);

		harness.Provider.RemoveMailbox("Projects/Client");
		await ReconcileAsync(harness);

		await harness.UsingAsync(async scope =>
		{
			var mailboxes = await scope.GetRequiredService<MyloMailDbContext>()
				.Mailboxes
				.ToListAsync();
			var projects = mailboxes.Single(mailbox => mailbox.ProviderMailboxId == "Projects");
			var client = mailboxes.Single(mailbox =>
				mailbox.ProviderMailboxId is null && mailbox.Name == "Client"
			);
			var invoices = mailboxes.Single(mailbox =>
				mailbox.ProviderMailboxId == "Projects/Client/Invoices"
			);

			Assert.Equal(projects.Id, client.ParentId);
			Assert.Equal(client.Id, invoices.ParentId);
		});
	}

	[Fact]
	public async Task Gmail_topology_and_sync_state_roll_back_together_at_commit_boundary()
	{
		await using var harness = await SyncHarness.CreateAsync(ProviderShapes.Gmail);
		harness.Provider.AddMailbox("Projects/Client/Invoices");
		harness.Faults.ArmAt(FaultPoints.TopologyAfterApplyBeforeCommit);

		await Assert.ThrowsAsync<SimulatedCrashException>(() => ReconcileAsync(harness));
		await harness.RestartAsync();

		await harness.UsingAsync(async scope =>
		{
			var context = scope.GetRequiredService<MyloMailDbContext>();
			Assert.Empty(await context.Mailboxes.ToListAsync());
			Assert.Empty(await context.MailboxTopologySyncStates.ToListAsync());
		});

		await ReconcileAsync(harness);
		await harness.UsingAsync(async scope =>
		{
			var context = scope.GetRequiredService<MyloMailDbContext>();
			Assert.Equal(3, await context.Mailboxes.CountAsync());
			Assert.Single(await context.MailboxTopologySyncStates.ToListAsync());
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
		harness.Provider.SeedMessage("INBOX", Guid.NewGuid(), DateTimeOffset.UnixEpoch);
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
			// A resync re-walks the same backlog from scratch — MessagesFetched must reset
			// alongside Status/ResumeToken, or the re-ingested messages double-count on top
			// of what was already fetched before the resync, inflating the sidebar's "N of
			// M" backfill progress past the real total.
			Assert.Equal(0, coverage.MessagesFetched);
			Assert.NotNull(await context.IntegrityReconciliationStates.SingleOrDefaultAsync());
		});
	}

	[Fact]
	public async Task Gmail_cursor_invalidation_discards_staged_history_from_the_expired_baseline()
	{
		await using var harness = await SyncHarness.CreateAsync(ProviderShapes.Gmail);
		harness.Provider.AddMailbox("INBOX", SpecialUse.Inbox);
		harness.Provider.SeedMessage("INBOX", Guid.NewGuid(), DateTimeOffset.UnixEpoch);
		await ReconcileAsync(harness);
		await SyncAsync(harness);

		await harness.UsingAsync(async scope =>
			Assert.NotEmpty(await scope.GetRequiredService<MyloMailDbContext>().StagedChangeEvents.ToListAsync())
		);

		harness.Provider.InvalidateCursors();
		var outcome = await SyncAsync(harness);

		Assert.True(outcome.ResyncTriggered);
		await harness.UsingAsync(async scope =>
		{
			var context = scope.GetRequiredService<MyloMailDbContext>();
			Assert.Empty(await context.StagedChangeEvents.ToListAsync());
			Assert.True((await context.ChangeStreamStates.SingleAsync()).IsRebasing);
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
		await SyncAsync(harness, "INBOX");
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

	/// <summary>
	/// §3: "a worker that was already running when an account was removed cannot commit for
	/// it." ReplayStagedAsync's own loop can span many staged pages and commits, so the
	/// account's IsEnabled must be re-checked every iteration, not just once by the caller
	/// before the method started — otherwise disabling the account mid-replay wouldn't stop
	/// later pages in the same call from still committing.
	/// </summary>
	[Fact]
	public async Task Disabling_the_account_mid_replay_stops_further_staged_pages_from_committing()
	{
		await using var harness = await SyncHarness.CreateAsync(ProviderShapes.Gmail);
		harness.Provider.AddMailbox("INBOX", SpecialUse.Inbox);
		await ReconcileAsync(harness);

		harness.Provider.SeedMessage("INBOX", Guid.NewGuid(), DateTimeOffset.UnixEpoch);
		harness.Provider.SeedMessage("INBOX", Guid.NewGuid(), DateTimeOffset.UnixEpoch.AddSeconds(1));
		await SyncAsync(harness);
		await CoverAsync(harness);

		var (stagedCountBeforeReplay, messageCountBeforeReplay) = await harness.UsingAsync(async scope =>
		{
			var context = scope.GetRequiredService<MyloMailDbContext>();
			return (
				await context.StagedChangeEvents.CountAsync(),
				await context.Messages.CountAsync()
			);
		});
		Assert.True(stagedCountBeforeReplay >= 1);

		// Disabled before replay runs at all — the very first iteration's check must catch this.
		await harness.UsingAsync(async scope =>
		{
			var context = scope.GetRequiredService<MyloMailDbContext>();
			var account = await context.Accounts.SingleAsync();
			account.IsEnabled = false;
			await context.SaveChangesAsync();
		});

		var replayed = await harness.UsingAsync(async scope =>
			await scope
				.GetRequiredService<ChangeStreamService>()
				.ReplayStagedAsync(await harness.AccountInScopeAsync(scope))
		);

		Assert.Equal(0, replayed);
		await harness.UsingAsync(async scope =>
		{
			var context = scope.GetRequiredService<MyloMailDbContext>();
			Assert.Equal(stagedCountBeforeReplay, await context.StagedChangeEvents.CountAsync());
			Assert.Equal(messageCountBeforeReplay, await context.Messages.CountAsync());
		});
	}

	/// <summary>
	/// The test above only proves the check catches an account already disabled before
	/// <see cref="ChangeStreamService.ReplayStagedAsync"/> is called at all — which the
	/// pre-existing caller-side check already handled. This test additionally proves the check
	/// is re-evaluated on a resumed replay attempt, not just remembered from before a crash: the
	/// first staged page commits, a simulated crash interrupts the call, the account is disabled
	/// while it is down, and a fresh call to resume the replay must still see the disable and
	/// refuse the remaining page rather than trusting whatever was true when the operation
	/// originally started.
	/// </summary>
	/// <remarks>
	/// This does not, on its own, distinguish "checked once per call, before the loop" from
	/// "checked on every loop iteration within one continuous call" — both placements behave
	/// identically here, since the resumed call only ever executes one iteration. The stricter
	/// per-iteration guarantee (disabling mid-way through a single uninterrupted call spanning
	/// several already-in-flight pages) is correct by inspection of
	/// <see cref="ChangeStreamService.ReplayStagedAsync"/>'s loop structure, but isn't
	/// independently exercised by a test: doing so would need a fault-injection mechanism that
	/// can run a side effect and let the same call continue, rather than only throwing to
	/// simulate a kill.
	/// </remarks>
	[Fact]
	public async Task Disabling_the_account_between_two_staged_pages_stops_only_the_later_one()
	{
		await using var harness = await SyncHarness.CreateAsync(ProviderShapes.Gmail);
		harness.Provider.AddMailbox("INBOX", SpecialUse.Inbox);
		await ReconcileAsync(harness);

		// Two separate incremental syncs so each stages its own row — a single SyncAsync call
		// with two seeded messages would stage them together as one page.
		harness.Provider.SeedMessage("INBOX", Guid.NewGuid(), DateTimeOffset.UnixEpoch);
		await SyncAsync(harness);
		harness.Provider.SeedMessage("INBOX", Guid.NewGuid(), DateTimeOffset.UnixEpoch.AddSeconds(1));
		await SyncAsync(harness);
		await CoverAsync(harness);

		var stagedCountBeforeReplay = await harness.UsingAsync(async scope =>
			await scope.GetRequiredService<MyloMailDbContext>().StagedChangeEvents.CountAsync()
		);
		Assert.Equal(2, stagedCountBeforeReplay);

		harness.Faults.ArmAt(FaultPoints.SyncPageAfterCommit);
		await Assert.ThrowsAsync<SimulatedCrashException>(() =>
			harness.UsingAsync(async scope =>
				await scope
					.GetRequiredService<ChangeStreamService>()
					.ReplayStagedAsync(await harness.AccountInScopeAsync(scope))
			)
		);

		// The first page's own commit already landed — a real crash cannot un-commit it.
		var (stagedAfterFirstPage, messagesAfterFirstPage) = await harness.UsingAsync(async scope =>
		{
			var context = scope.GetRequiredService<MyloMailDbContext>();
			return (await context.StagedChangeEvents.CountAsync(), await context.Messages.CountAsync());
		});
		Assert.Equal(stagedCountBeforeReplay - 1, stagedAfterFirstPage);

		// Disabled between the two pages of what would otherwise be one continuous replay.
		await harness.UsingAsync(async scope =>
		{
			var context = scope.GetRequiredService<MyloMailDbContext>();
			var account = await context.Accounts.SingleAsync();
			account.IsEnabled = false;
			await context.SaveChangesAsync();
		});

		var replayedAfterDisable = await harness.UsingAsync(async scope =>
			await scope
				.GetRequiredService<ChangeStreamService>()
				.ReplayStagedAsync(await harness.AccountInScopeAsync(scope))
		);

		Assert.Equal(0, replayedAfterDisable);
		await harness.UsingAsync(async scope =>
		{
			var context = scope.GetRequiredService<MyloMailDbContext>();
			// Still exactly the one remaining staged page — the second never replayed.
			Assert.Equal(stagedAfterFirstPage, await context.StagedChangeEvents.CountAsync());
			Assert.Equal(messagesAfterFirstPage, await context.Messages.CountAsync());
		});
	}

	/// <summary>
	/// §3, same shape as <see cref="ChangeStreamService.ReplayStagedAsync"/>'s own fix: a
	/// single <see cref="ChangeStreamService.SyncAsync"/> call can walk many pages of a large
	/// incremental sync (the fake provider pages at 50 messages), committing each one, so
	/// disabling the account partway through a multi-page walk must stop later pages from
	/// still committing — a check only when the enclosing job started would miss them.
	/// </summary>
	/// <remarks>
	/// As with the <c>ReplayStagedAsync</c> tests above, <see cref="ScriptedFaultInjector"/> can
	/// only throw to simulate a kill, not run a side effect mid-call and let the same call
	/// continue — so this proves the check is re-evaluated on a resumed sync attempt, not that
	/// it fires between two iterations of one uninterrupted call. The per-iteration placement
	/// itself is correct by inspection of the loop.
	/// </remarks>
	[Fact]
	public async Task Disabling_the_account_between_two_change_stream_pages_stops_the_later_one()
	{
		await using var harness = await SyncHarness.CreateAsync(ProviderShapes.Graph);
		harness.Provider.AddMailbox("INBOX", SpecialUse.Inbox);
		await ReconcileAsync(harness);
		for (var i = 0; i < 51; i++)
		{
			harness.Provider.SeedMessage("INBOX", Guid.NewGuid(), DateTimeOffset.UnixEpoch.AddSeconds(i));
		}

		harness.Faults.ArmAt(FaultPoints.SyncPageAfterCommit);
		await Assert.ThrowsAsync<SimulatedCrashException>(() => SyncAsync(harness));

		var (messagesAfterFirstPage, cursorAfterFirstPage) = await harness.UsingAsync(async scope =>
		{
			var context = scope.GetRequiredService<MyloMailDbContext>();
			return (
				await context.Messages.CountAsync(),
				await context.ChangeStreamStates.Select(s => s.CursorState).SingleAsync()
			);
		});
		// The first page's own commit already landed — a real crash cannot un-commit it — and
		// with only 50 (of 51) messages fetched so far, the cursor is still null (mid-walk).
		Assert.Equal(50, messagesAfterFirstPage);
		Assert.Null(cursorAfterFirstPage);

		await harness.UsingAsync(async scope =>
		{
			var context = scope.GetRequiredService<MyloMailDbContext>();
			var account = await context.Accounts.SingleAsync();
			account.IsEnabled = false;
			await context.SaveChangesAsync();
		});

		var outcome = await SyncAsync(harness);

		Assert.Equal(0, outcome.Pages);
		await harness.UsingAsync(async scope =>
		{
			var context = scope.GetRequiredService<MyloMailDbContext>();
			// Still 50 — the second page never committed the remaining message.
			Assert.Equal(messagesAfterFirstPage, await context.Messages.CountAsync());
		});
	}

	/// <summary>
	/// Same §3 requirement, same honest caveat as the two tests above, this time for
	/// <see cref="CalendarSyncService"/>'s own page loop (pass 184, extending pass 183's fix to
	/// its sibling sync loops): a single <see cref="CalendarSyncService.SynchronizeAsync"/> call
	/// can walk many calendar pages before a token is committed, so disabling the account
	/// partway through must stop later pages from still committing.
	/// </summary>
	[Fact]
	public async Task Disabling_the_account_between_two_calendar_sync_pages_stops_the_later_one()
	{
		await using var harness = await SyncHarness.CreateAsync(ProviderShapes.Imap(ImapCapabilityTier.QResync));
		harness.CalendarProvider.PagesRemaining = 2;

		harness.Faults.ArmAt(FaultPoints.SyncPageAfterCommit);
		await Assert.ThrowsAsync<SimulatedCrashException>(() =>
			harness.UsingAsync(async scope =>
				await scope
					.GetRequiredService<CalendarSyncService>()
					.SynchronizeAsync(await harness.AccountInScopeAsync(scope))
			)
		);

		// The first page's own commit already landed — a real crash cannot un-commit it — and
		// with a continuation still pending, no cursor has been committed to the calendar yet.
		var eventsAfterFirstPage = await harness.UsingAsync(async scope =>
		{
			var context = scope.GetRequiredService<MyloMailDbContext>();
			Assert.Null((await context.Calendars.SingleAsync()).SyncCursor);
			return await context.CalendarEvents.CountAsync();
		});
		Assert.Equal(1, eventsAfterFirstPage);

		await harness.UsingAsync(async scope =>
		{
			var context = scope.GetRequiredService<MyloMailDbContext>();
			var account = await context.Accounts.SingleAsync();
			account.IsEnabled = false;
			await context.SaveChangesAsync();
		});

		await harness.UsingAsync(async scope =>
			await scope
				.GetRequiredService<CalendarSyncService>()
				.SynchronizeAsync(await harness.AccountInScopeAsync(scope))
		);

		await harness.UsingAsync(async scope =>
		{
			var context = scope.GetRequiredService<MyloMailDbContext>();
			// Still just the one event from the first page — the second page never committed.
			Assert.Equal(eventsAfterFirstPage, await context.CalendarEvents.CountAsync());
			Assert.Null((await context.Calendars.SingleAsync()).SyncCursor);
		});
	}

	/// <summary>
	/// The change stream applies what it observes. Coverage is a separate concern, and a test
	/// that reaches the local rows through backfill proves nothing about incremental sync.
	/// </summary>
	[Fact]
	public async Task The_change_stream_applies_server_side_flag_changes()
	{
		await using var harness = await SyncHarness.CreateAsync(ProviderShapes.Graph);
		harness.Provider.AddMailbox("INBOX", SpecialUse.Inbox);
		var occurrenceId = harness.Provider.SeedMessage("INBOX", Guid.NewGuid(), DateTimeOffset.UnixEpoch);

		await ReconcileAsync(harness);
		await CoverAsync(harness);

		var messageId = await harness.UsingAsync(async scope =>
			(await scope.GetRequiredService<MyloMailDbContext>().Messages.SingleAsync()).Id
		);
		Assert.False(await IsReadAsync(harness, messageId));

		// Another client marks it read.
		await harness.UsingAsync(async scope =>
			await harness.Provider.SetFlagsAsync(
				await harness.AccountInScopeAsync(scope),
				[new MessageOccurrenceRef(messageId, Guid.Empty, occurrenceId)],
				new FlagUpdate(IsRead: true, IsFlagged: null),
				default
			)
		);

		await SyncAsync(harness);

		Assert.True(await IsReadAsync(harness, messageId));
	}

	/// <summary>
	/// A removal removes the membership and never the canonical message: under Graph's
	/// folder-scoped delta a move surfaces as a removal and an addition in either order, so a
	/// message may legitimately have no memberships for a moment.
	/// </summary>
	[Fact]
	public async Task The_change_stream_removes_a_membership_without_deleting_the_message()
	{
		await using var harness = await SyncHarness.CreateAsync(ProviderShapes.Graph);
		harness.Provider.AddMailbox("INBOX", SpecialUse.Inbox);
		var occurrenceId = harness.Provider.SeedMessage("INBOX", Guid.NewGuid(), DateTimeOffset.UnixEpoch);

		await ReconcileAsync(harness);
		await CoverAsync(harness);

		var messageId = await harness.UsingAsync(async scope =>
			(await scope.GetRequiredService<MyloMailDbContext>().Messages.SingleAsync()).Id
		);

		await harness.UsingAsync(async scope =>
			await harness.Provider.RemoveFromMailboxAsync(
				await harness.AccountInScopeAsync(scope),
				[new MessageOccurrenceRef(messageId, Guid.Empty, occurrenceId)],
				default
			)
		);

		await SyncAsync(harness);

		await harness.UsingAsync(async scope =>
		{
			var context = scope.GetRequiredService<MyloMailDbContext>();
			Assert.Empty(await context.MessageMailboxes.ToListAsync());
			Assert.NotEmpty(await context.Messages.ToListAsync());
		});
	}

	/// <summary>
	/// A QRESYNC VANISHED removal and its mod-sequence cursor commit atomically. A cursor
	/// committed without the removal makes the server correctly omit that UID on replay,
	/// silently preserving a membership that no longer exists remotely.
	/// </summary>
	[Fact]
	public async Task A_crash_before_a_qresync_removal_commit_replays_the_vanished_uid()
	{
		await using var harness = await SyncHarness.CreateAsync(
			ProviderShapes.Imap(ImapCapabilityTier.QResync)
		);
		harness.Provider.AddMailbox("INBOX", SpecialUse.Inbox);
		var occurrenceId = harness.Provider.SeedMessage(
			"INBOX",
			Guid.NewGuid(),
			DateTimeOffset.UnixEpoch
		);
		await ReconcileAsync(harness);
		await CoverAsync(harness);
		await SyncAsync(harness);

		var (messageId, baselineModSeq) = await harness.UsingAsync(async scope =>
		{
			var context = scope.GetRequiredService<MyloMailDbContext>();
			var message = await context.Messages.SingleAsync();
			var state = await context.ChangeStreamStates.SingleAsync();
			var cursor = Assert.IsType<ImapUidCursor>(state.CursorState);
			Assert.NotNull(cursor.HighestModSeq);
			return (message.Id, cursor.HighestModSeq.Value);
		});
		await harness.UsingAsync(async scope =>
			await harness.Provider.RemoveFromMailboxAsync(
				await harness.AccountInScopeAsync(scope),
				[new MessageOccurrenceRef(messageId, Guid.Empty, occurrenceId)],
				default
			)
		);

		harness.Faults.ArmAt(FaultPoints.SyncPageAfterApplyBeforeCommit);
		await Assert.ThrowsAsync<SimulatedCrashException>(() => SyncAsync(harness));
		await harness.RestartAsync();

		await harness.UsingAsync(async scope =>
		{
			var context = scope.GetRequiredService<MyloMailDbContext>();
			Assert.Single(await context.MessageMailboxes.ToListAsync());
			var state = await context.ChangeStreamStates.SingleAsync();
			var cursor = Assert.IsType<ImapUidCursor>(state.CursorState);
			Assert.Equal(baselineModSeq, cursor.HighestModSeq);
		});

		await SyncAsync(harness);
		await harness.UsingAsync(async scope =>
			Assert.Empty(
				await scope.GetRequiredService<MyloMailDbContext>().MessageMailboxes.ToListAsync()
			)
		);
	}

	/// <summary>
	/// Staging still advances the cursor, and must: the page is durably persisted, just not
	/// yet applied. Leaving the cursor behind would re-drain the same history on every run
	/// and never let the account finish its baseline.
	/// </summary>
	[Fact]
	public async Task Staging_a_page_advances_the_cursor()
	{
		await using var harness = await SyncHarness.CreateAsync(ProviderShapes.Gmail);
		harness.Provider.AddMailbox("INBOX", SpecialUse.Inbox);
		harness.Provider.SeedMessage("INBOX", Guid.NewGuid(), DateTimeOffset.UnixEpoch);
		await ReconcileAsync(harness);

		Assert.True((await SyncAsync(harness)).Staged);

		await harness.UsingAsync(async scope =>
		{
			var context = scope.GetRequiredService<MyloMailDbContext>();
			var state = await context.ChangeStreamStates.SingleAsync();

			Assert.NotNull(state.CursorState);
			Assert.NotNull(state.BaselineEstablishedAt);
		});
	}

	private static Task<bool> IsReadAsync(SyncHarness harness, Guid messageId) =>
		harness.UsingAsync(async scope =>
			(await scope.GetRequiredService<MyloMailDbContext>().Messages.SingleAsync(m => m.Id == messageId)).IsRead
		);

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

	private static byte[] MimeBytes()
	{
		var message = new MimeMessage();
		message.From.Add(MailboxAddress.Parse("author@example.test"));
		message.To.Add(MailboxAddress.Parse("recipient@example.test"));
		message.Subject = "Remote draft";
		message.Body = new TextPart("html") { Text = "<p>Body</p>" };
		using var stream = new MemoryStream();
		message.WriteTo(stream);
		return stream.ToArray();
	}
}
