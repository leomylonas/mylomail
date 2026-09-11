using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MyloMail.Api.Domain;
using MyloMail.Api.Persistence;
using MyloMail.Api.Providers;
using MyloMail.Api.Sync;
using MyloMail.Api.Tests.Fakes;
using Xunit;

namespace MyloMail.Api.Tests.Sync;

/// <summary>
/// §7: every sync concern ends by announcing the mailboxes whose counts it moved. The sidebar
/// count is the only thing that says how much mail a folder holds, and nothing else refreshes
/// it — a concern that changes memberships silently leaves the number the user reads wrong
/// until some unrelated job happens to announce the same mailbox.
/// </summary>
public sealed class MailboxAnnouncementTests
{
	/// <summary>
	/// Gmail's stream is account-scoped and its loop owns one arbitrary mailbox, so the
	/// mailbox being polled says nothing about which labels a page changed.
	/// </summary>
	[Fact]
	public async Task An_account_scoped_page_announces_the_mailboxes_it_filled_not_the_one_polled()
	{
		await using var harness = await SyncHarness.CreateAsync(ProviderShapes.Gmail);
		harness.Provider.AddMailbox("INBOX", SpecialUse.Inbox);
		harness.Provider.AddMailbox("ARCHIVE", SpecialUse.Archive);
		harness.Provider.AddMailbox("RECEIPTS");
		await SyncTests.ReconcileAsync(harness);

		// Gmail stages history until coverage completes, so every mailbox is covered first;
		// what follows is a genuine steady-state page.
		await SyncTests.SyncAsync(harness);
		await SyncTests.CoverAsync(harness);
		await SyncTests.CoverAsync(harness, "ARCHIVE");
		await SyncTests.CoverAsync(harness, "RECEIPTS");

		harness.Provider.SeedMessage("ARCHIVE", Guid.NewGuid(), DateTimeOffset.UnixEpoch);
		harness.Events.Clear();

		var outcome = await SyncTests.SyncAsync(harness);
		Assert.False(outcome.Staged);

		var (archiveId, receiptsId) = await harness.UsingAsync(async scope =>
		{
			var context = scope.GetRequiredService<MyloMailDbContext>();
			return (
				(await context.Mailboxes.SingleAsync(m => m.ProviderMailboxId == "ARCHIVE")).Id,
				(await context.Mailboxes.SingleAsync(m => m.ProviderMailboxId == "RECEIPTS")).Id
			);
		});

		var announced = harness.Events.Mailboxes.Select(mailbox => mailbox.Id).ToList();
		Assert.Contains(archiveId, announced);

		// The mailbox nothing landed in is the discriminator: announcing every mailbox on the
		// account would satisfy the assertion above while telling a listener nothing.
		Assert.DoesNotContain(receiptsId, announced);
	}

	/// <summary>
	/// A staged page advances the cursor without touching a membership, so the mailboxes it
	/// fills exist only once replay applies it (§3).
	/// </summary>
	[Fact]
	public async Task Staged_replay_announces_the_mailbox_whose_count_it_finally_moved()
	{
		await using var harness = await SyncHarness.CreateAsync(ProviderShapes.Gmail);
		harness.Provider.AddMailbox("INBOX", SpecialUse.Inbox);
		await SyncTests.ReconcileAsync(harness);
		var occurrence = harness.Provider.SeedMessage("INBOX", Guid.NewGuid(), DateTimeOffset.UnixEpoch);
		Assert.True((await SyncTests.SyncAsync(harness)).Staged);

		// Deleted on the server before backfill reached it, so coverage fetches nothing and
		// the staged arrival is the only thing that can create the membership — the case where
		// replay, not backfill, is what moves the count.
		harness.Provider.RemoveMessage(occurrence);
		await SyncTests.CoverAsync(harness);

		var inboxId = await harness.UsingAsync(async scope =>
			(await scope.GetRequiredService<MyloMailDbContext>().Mailboxes.SingleAsync()).Id
		);
		harness.Events.Clear();

		var replayed = await harness.UsingAsync(async scope =>
			await scope
				.GetRequiredService<ChangeStreamService>()
				.ReplayStagedAsync(await harness.AccountInScopeAsync(scope))
		);

		Assert.True(replayed > 0);
		Assert.Equal(inboxId, Assert.Single(harness.Events.Mailboxes).Id);
	}

	/// <summary>
	/// Periodic reconciliation is the only thing that observes an expunge a degraded IMAP
	/// server never reported, so it is also the only thing that can correct the count.
	/// </summary>
	[Fact]
	public async Task Reconciling_away_a_stale_occurrence_announces_the_mailbox()
	{
		await using var harness = await SyncHarness.CreateAsync(
			ProviderShapes.Imap(ImapCapabilityTier.CondStore)
		);
		harness.Provider.AddMailbox("INBOX", SpecialUse.Inbox);
		var occurrence = harness.Provider.SeedMessage("INBOX", Guid.NewGuid(), DateTimeOffset.UnixEpoch);
		await SyncTests.ReconcileAsync(harness);
		await SyncTests.CoverAsync(harness);

		harness.Provider.RemoveMessage(occurrence);
		harness.Events.Clear();

		var mailboxId = await harness.UsingAsync(async services =>
		{
			var context = services.GetRequiredService<MyloMailDbContext>();
			var mailbox = await context.Mailboxes.SingleAsync();
			await services
				.GetRequiredService<IntegrityReconciliationService>()
				.ReconcileAsync(await harness.AccountInScopeAsync(services), mailbox);
			return mailbox.Id;
		});

		var announced = Assert.Single(harness.Events.Mailboxes);
		Assert.Equal(mailboxId, announced.Id);
		Assert.Equal(0, announced.LocalCount);
	}

	/// <summary>
	/// A poll that observed nothing new must not announce a count: IMAP reports its whole
	/// mailbox on every poll, so an event keyed on "the provider mentioned this mailbox"
	/// fires forever and means nothing (§7).
	/// </summary>
	[Fact]
	public async Task A_reconciliation_that_removed_nothing_announces_no_mailbox()
	{
		await using var harness = await SyncHarness.CreateAsync(
			ProviderShapes.Imap(ImapCapabilityTier.CondStore)
		);
		harness.Provider.AddMailbox("INBOX", SpecialUse.Inbox);
		harness.Provider.SeedMessage("INBOX", Guid.NewGuid(), DateTimeOffset.UnixEpoch);
		await SyncTests.ReconcileAsync(harness);
		await SyncTests.CoverAsync(harness);
		harness.Events.Clear();

		await harness.UsingAsync(async services =>
		{
			var context = services.GetRequiredService<MyloMailDbContext>();
			await services
				.GetRequiredService<IntegrityReconciliationService>()
				.ReconcileAsync(
					await harness.AccountInScopeAsync(services),
					await context.Mailboxes.SingleAsync()
				);
		});

		Assert.Empty(harness.Events.Mailboxes);
	}
}
