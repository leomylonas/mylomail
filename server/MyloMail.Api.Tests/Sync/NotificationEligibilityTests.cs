using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MyloMail.Api.Domain;
using MyloMail.Api.Hubs;
using MyloMail.Api.Persistence;
using MyloMail.Api.Providers;
using MyloMail.Api.Sync;
using MyloMail.Api.Tests.Fakes;
using Xunit;

namespace MyloMail.Api.Tests.Sync;

/// <summary>
/// Notification eligibility (§13 Epic 9): a canonical row's creation is not itself the
/// signal, and eligibility depends on stream provenance, not on when a test happens to look.
/// </summary>
/// <remarks>
/// <see cref="SyncHarness"/>'s clock is frozen at <see cref="DateTimeOffset.UnixEpoch"/>, which
/// is exactly when a stream's <c>NotificationBaselineAt</c> and an account's
/// <c>NotificationEpoch</c> get stamped here — so a "backlog" message has to be dated strictly
/// before that instant, not at it, or it collides with the cutoff instead of predating it.
/// </remarks>
public sealed class NotificationEligibilityTests
{
	private static readonly DateTimeOffset Backlog = DateTimeOffset.UnixEpoch.AddSeconds(-1);

	[Fact]
	public async Task A_streams_first_page_establishes_its_baseline_without_notifying()
	{
		await using var harness = await SyncHarness.CreateAsync(ProviderShapes.Imap(ImapCapabilityTier.QResync));
		harness.Provider.AddMailbox("INBOX", SpecialUse.Inbox);
		harness.Provider.SeedMessage("INBOX", Guid.NewGuid(), Backlog);
		await SyncTests.ReconcileAsync(harness);

		await SyncTests.SyncAsync(harness);

		// A message was genuinely created — this is not a test of ingestion — but the very
		// first page a stream ever applies must not treat its own catch-up as new mail.
		await harness.UsingAsync(async scope =>
			Assert.NotEmpty(await scope.GetRequiredService<MyloMailDbContext>().Messages.ToListAsync())
		);
		Assert.Empty(harness.Events.Notifications);
	}

	[Fact]
	public async Task A_message_arriving_after_the_baseline_is_established_notifies()
	{
		await using var harness = await SyncHarness.CreateAsync(ProviderShapes.Imap(ImapCapabilityTier.QResync));
		harness.Provider.AddMailbox("INBOX", SpecialUse.Inbox);
		harness.Provider.SeedMessage("INBOX", Guid.NewGuid(), Backlog);
		await SyncTests.ReconcileAsync(harness);
		await SyncTests.SyncAsync(harness);
		Assert.Empty(harness.Events.Notifications);

		// Basic-tier IMAP re-reports its whole mailbox on every poll — the fake provider does
		// too — so the backlog message is observed again here, unchanged, alongside the new
		// one. The stream's fixed baseline is what keeps it excluded a second time.
		harness.Provider.SeedMessage("INBOX", Guid.NewGuid(), DateTimeOffset.UnixEpoch.AddMinutes(1));
		await SyncTests.SyncAsync(harness);

		var notification = Assert.Single(harness.Events.Notifications);
		Assert.Equal(harness.Account.Id, notification.AccountId);
	}

	[Fact]
	public async Task A_message_dated_before_the_accounts_notification_epoch_does_not_notify()
	{
		await using var harness = await SyncHarness.CreateAsync(ProviderShapes.Imap(ImapCapabilityTier.QResync));
		harness.Provider.AddMailbox("INBOX", SpecialUse.Inbox);
		harness.Provider.SeedMessage("INBOX", Guid.NewGuid(), Backlog);
		await SyncTests.ReconcileAsync(harness);
		await SyncTests.SyncAsync(harness);

		// The epoch moves ahead of the next message's date: an account resynchronisation
		// finding old mail through a fresh stream must not treat it as news either, even
		// though that message is after the stream's own (unrelated) baseline.
		await harness.UsingAsync(async scope =>
		{
			var context = scope.GetRequiredService<MyloMailDbContext>();
			var account = await context.Accounts.SingleAsync(a => a.Id == harness.Account.Id);
			account.NotificationEpoch = DateTimeOffset.UnixEpoch.AddDays(1);
			await context.SaveChangesAsync();
		});
		harness.Provider.SeedMessage("INBOX", Guid.NewGuid(), DateTimeOffset.UnixEpoch.AddMinutes(1));

		await SyncTests.SyncAsync(harness);

		Assert.Empty(harness.Events.Notifications);
	}

	[Fact]
	public async Task Disabled_account_notifications_never_fire()
	{
		await using var harness = await SyncHarness.CreateAsync(ProviderShapes.Imap(ImapCapabilityTier.QResync));
		harness.Provider.AddMailbox("INBOX", SpecialUse.Inbox);
		harness.Provider.SeedMessage("INBOX", Guid.NewGuid(), Backlog);
		await SyncTests.ReconcileAsync(harness);
		await SyncTests.SyncAsync(harness);

		await harness.UsingAsync(async scope =>
		{
			var context = scope.GetRequiredService<MyloMailDbContext>();
			var account = await context.Accounts.SingleAsync(a => a.Id == harness.Account.Id);
			account.NotificationsEnabled = false;
			await context.SaveChangesAsync();
		});
		harness.Provider.SeedMessage("INBOX", Guid.NewGuid(), DateTimeOffset.UnixEpoch.AddMinutes(1));

		await SyncTests.SyncAsync(harness);

		Assert.Empty(harness.Events.Notifications);
	}

	/// <summary>
	/// A triggered resynchronisation must not silently drop a notification for mail that
	/// arrives during the resync's own catch-up window (§13 Epic 9): the baseline is captured
	/// at the moment resync is triggered, not after that first page finishes applying.
	/// </summary>
	[Fact]
	public async Task Mail_arriving_in_a_resyncs_own_first_page_still_notifies()
	{
		await using var harness = await SyncHarness.CreateAsync(ProviderShapes.Imap(ImapCapabilityTier.QResync));
		harness.Provider.AddMailbox("INBOX", SpecialUse.Inbox);
		harness.Provider.SeedMessage("INBOX", Guid.NewGuid(), Backlog);
		await SyncTests.ReconcileAsync(harness);
		await SyncTests.SyncAsync(harness);
		Assert.Empty(harness.Events.Notifications);

		// Real time passes before the resync is triggered, and more still passes while the
		// resync's own catch-up work runs — a frozen clock could not tell "baseline captured
		// at trigger" apart from "baseline captured once catch-up finishes", so this advances
		// it to make the two moments genuinely distinct.
		harness.Clock.Advance(TimeSpan.FromHours(1));
		harness.Provider.InvalidateCursors();
		// A run against an invalidated cursor only triggers the resync and returns; it does
		// not itself re-fetch.
		await SyncTests.SyncAsync(harness);

		// Arrives right after the resync was triggered — inside its catch-up window, before
		// the re-fetch below actually runs.
		harness.Provider.SeedMessage("INBOX", Guid.NewGuid(), harness.Clock.GetUtcNow().AddSeconds(1));
		harness.Clock.Advance(TimeSpan.FromMinutes(10));

		await SyncTests.SyncAsync(harness);

		// The re-fetched backlog message is excluded by the same fixed baseline; only the one
		// that arrived after resync was triggered notifies — even though the fetch that found
		// it happened ten minutes later.
		Assert.Single(harness.Events.Notifications);
	}

	/// <summary>
	/// §3 states this outright: "otherwise live mail would go unnotified for the entire
	/// backfill — potentially hours on a large account". A notification for genuinely new
	/// mail must not wait for replay to catch up on a Gmail account still backfilling.
	/// </summary>
	[Fact]
	public async Task A_staged_arrival_notifies_immediately_without_waiting_for_replay()
	{
		await using var harness = await SyncHarness.CreateAsync(ProviderShapes.Gmail);
		harness.Provider.AddMailbox("INBOX", SpecialUse.Inbox);
		await SyncTests.ReconcileAsync(harness);

		// Coverage has not run, so this page stages rather than applies — and the stream's
		// state (and its fixed notification baseline) already exists from reconciliation.
		harness.Provider.SeedMessage("INBOX", Guid.NewGuid(), DateTimeOffset.UnixEpoch);
		await SyncTests.SyncAsync(harness);

		// Notified from the staged page itself — replay has not run at all yet.
		var notification = Assert.Single(harness.Events.Notifications);
		Assert.Null(notification.MessageId);
		await harness.UsingAsync(async scope =>
			Assert.Empty(await scope.GetRequiredService<MyloMailDbContext>().Messages.ToListAsync())
		);

		await SyncTests.CoverAsync(harness);
		var replayed = await harness.UsingAsync(async scope =>
			await scope
				.GetRequiredService<ChangeStreamService>()
				.ReplayStagedAsync(await harness.AccountInScopeAsync(scope))
		);
		Assert.True(replayed > 0);

		// Replay does not announce it a second time, and links the record to the row it just
		// materialised so a later click can navigate straight to it.
		Assert.Single(harness.Events.Notifications);
		await harness.UsingAsync(async scope =>
		{
			var context = scope.GetRequiredService<MyloMailDbContext>();
			var message = await context.Messages.SingleAsync();

			var record = await context.NotificationRecords.SingleAsync(n => n.Id == notification.Id);
			Assert.Equal(message.Id, record.MessageId);
		});
	}

	/// <summary>
	/// Coverage/backfill can materialise a message's canonical row independently of replay,
	/// racing a still-unlinked staged notification for the same message — and once coverage
	/// finishes, the very next sync page for it runs the ordinary (non-staged) path, which
	/// must recognise that pending record rather than inserting a second one.
	/// </summary>
	[Fact]
	public async Task A_message_backfilled_by_coverage_before_replay_still_notifies_once()
	{
		await using var harness = await SyncHarness.CreateAsync(ProviderShapes.Gmail);
		harness.Provider.AddMailbox("INBOX", SpecialUse.Inbox);
		await SyncTests.ReconcileAsync(harness);

		harness.Provider.SeedMessage("INBOX", Guid.NewGuid(), DateTimeOffset.UnixEpoch);
		await SyncTests.SyncAsync(harness);
		Assert.Single(harness.Events.Notifications);

		// Coverage materialises the same message directly — replay has not run, so the
		// pending notification recorded above is still keyed only by provider stable id.
		await SyncTests.CoverAsync(harness);

		// Coverage is now complete, so this page takes the ordinary path, not staging, and
		// reports the same message again.
		await SyncTests.SyncAsync(harness);

		Assert.Single(harness.Events.Notifications);
		await harness.UsingAsync(async scope =>
		{
			var context = scope.GetRequiredService<MyloMailDbContext>();
			var message = await context.Messages.SingleAsync();
			var record = await context.NotificationRecords.SingleAsync();
			Assert.Equal(message.Id, record.MessageId);
		});
	}

	/// <summary>
	/// Clicking a staged notification before account coverage completes reports that it is
	/// pending: replaying staged Gmail history first could be overwritten by a later stale
	/// coverage page. The renderer can therefore keep retrying instead of treating "not ready"
	/// as a missing message.
	/// </summary>
	[Fact]
	public async Task Resolving_a_staged_notification_waits_for_account_coverage()
	{
		await using var harness = await SyncHarness.CreateAsync(ProviderShapes.Gmail);
		var inbox = harness.Provider.AddMailbox("INBOX", SpecialUse.Inbox);
		await SyncTests.ReconcileAsync(harness);

		var occurrenceId = harness.Provider.SeedMessage("INBOX", Guid.NewGuid(), DateTimeOffset.UnixEpoch);
		inbox.Messages[occurrenceId].Subject = "A routed notification";
		inbox.Messages[occurrenceId].From = [new Address("Ada", "ada@example.test")];
		await SyncTests.SyncAsync(harness);
		var notification = Assert.Single(harness.Events.Notifications);
		Assert.Null(notification.MessageId);

		// Deliberately no coverage run first: account-wide Gmail history must remain staged
		// until every mailbox has its baseline snapshot.
		await harness.UsingAsync(async scope =>
			Assert.Empty(await scope.GetRequiredService<MyloMailDbContext>().Messages.ToListAsync())
		);

		var unresolved = await harness.UsingAsync(async scope =>
		{
			var hub = ActivatorUtilities.CreateInstance<MailHub>(scope);
			return await hub.ResolveNotificationNavigation(notification.Id);
		});
		Assert.NotNull(unresolved);
		Assert.Equal(NotificationNavigationStatus.Pending, unresolved.Status);
		Assert.Equal(harness.Account.Id, unresolved.AccountId);
		Assert.Null(unresolved.MessageId);
		Assert.Null(unresolved.MailboxId);

		await SyncTests.CoverAsync(harness);
		var resolved = await harness.UsingAsync(async scope =>
		{
			var hub = ActivatorUtilities.CreateInstance<MailHub>(scope);
			return await hub.ResolveNotificationNavigation(notification.Id);
		});

		Assert.NotNull(resolved);
		Assert.Equal(NotificationNavigationStatus.Ready, resolved.Status);
		Assert.Equal(harness.Account.Id, resolved.AccountId);
		Assert.Equal("A routed notification", resolved.Subject);
		Assert.Equal("ada@example.test", resolved.SenderAddress);
		await harness.UsingAsync(async scope =>
		{
			var context = scope.GetRequiredService<MyloMailDbContext>();
			var message = await context.Messages.SingleAsync();
			var mailbox = await context.Mailboxes.SingleAsync(candidate => candidate.SpecialUse == SpecialUse.Inbox);
			Assert.Equal(message.Id, resolved.MessageId);
			Assert.Equal(mailbox.Id, resolved.MailboxId);
		});

		// A second call, once already resolved, returns the same canonical route without
		// draining anything further.
		var resolvedAgain = await harness.UsingAsync(async scope =>
		{
			var hub = ActivatorUtilities.CreateInstance<MailHub>(scope);
			return await hub.ResolveNotificationNavigation(notification.Id);
		});
		Assert.Equal(resolved, resolvedAgain);
	}

	[Fact]
	public async Task An_orphaned_staged_notification_becomes_unavailable_after_replacement_coverage()
	{
		await using var harness = await SyncHarness.CreateAsync(ProviderShapes.Gmail);
		harness.Provider.AddMailbox("INBOX", SpecialUse.Inbox);
		await SyncTests.ReconcileAsync(harness);

		var occurrenceId = harness.Provider.SeedMessage(
			"INBOX",
			Guid.NewGuid(),
			DateTimeOffset.UnixEpoch
		);
		await SyncTests.SyncAsync(harness);
		var notification = Assert.Single(harness.Events.Notifications);

		// Cursor invalidation discards the old staged epoch. The provider then no longer has
		// the message, so replacement coverage cannot link this durable notification.
		harness.Provider.InvalidateCursors();
		var invalidated = await SyncTests.SyncAsync(harness);
		Assert.True(invalidated.ResyncTriggered);
		harness.Provider.RemoveMessage(occurrenceId);
		await SyncTests.SyncAsync(harness);
		await SyncTests.CoverAsync(harness);

		var navigation = await harness.UsingAsync(async scope =>
		{
			var hub = ActivatorUtilities.CreateInstance<MailHub>(scope);
			var resolved = await hub.ResolveNotificationNavigation(notification.Id);
			Assert.Empty(await scope.GetRequiredService<MyloMailDbContext>().StagedChangeEvents.ToListAsync());
			return resolved;
		});

		Assert.Null(navigation);
	}

	[Fact]
	public async Task Notification_navigation_prefers_the_effective_inbox_when_a_message_has_several_labels()
	{
		await using var harness = await SyncHarness.CreateAsync(ProviderShapes.Gmail);
		harness.Provider.AddMailbox("Archive");
		harness.Provider.AddMailbox("INBOX", SpecialUse.Inbox);
		await SyncTests.ReconcileAsync(harness);

		var (navigation, inboxId) = await harness.UsingAsync(async scope =>
		{
			var context = scope.GetRequiredService<MyloMailDbContext>();
			var archive = await context.Mailboxes.SingleAsync(mailbox => mailbox.Name == "Archive");
			var inbox = await context.Mailboxes.SingleAsync(mailbox => mailbox.SpecialUse == SpecialUse.Inbox);
			var message = new Message
			{
				Id = Guid.NewGuid(),
				AccountId = harness.Account.Id,
				Subject = "Labelled message",
				From = [new Address("Grace", "grace@example.test")],
				ReceivedAt = DateTimeOffset.UnixEpoch,
			};
			var notification = new NotificationRecord
			{
				Id = Guid.NewGuid(),
				AccountId = harness.Account.Id,
				MessageId = message.Id,
				Kind = NotificationKind.NewMessage,
				CreatedAt = DateTimeOffset.UnixEpoch,
			};
			context.AddRange(
				message,
				new MessageMailbox
				{
					Id = Guid.NewGuid(),
					MessageId = message.Id,
					MailboxId = archive.Id,
					ProviderOccurrenceId = "archive-occurrence",
				},
				new MessageMailbox
				{
					Id = Guid.NewGuid(),
					MessageId = message.Id,
					MailboxId = inbox.Id,
					ProviderOccurrenceId = "inbox-occurrence",
				},
				notification
			);
			await context.SaveChangesAsync();

			var hub = ActivatorUtilities.CreateInstance<MailHub>(scope);
			return (await hub.ResolveNotificationNavigation(notification.Id), inbox.Id);
		});

		Assert.NotNull(navigation);
		Assert.Equal(NotificationNavigationStatus.Ready, navigation.Status);
		Assert.Equal(inboxId, navigation.MailboxId);
		Assert.Equal("Labelled message", navigation.Subject);
		Assert.Equal("grace@example.test", navigation.SenderAddress);
	}
}
