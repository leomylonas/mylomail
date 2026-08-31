using Microsoft.Extensions.DependencyInjection;
using MyloMail.Api.Domain;
using MyloMail.Api.Tests.Fakes;
using Xunit;

namespace MyloMail.Api.Tests.Sync;

/// <summary>
/// Which §7 event a sync raises, and when it raises nothing.
/// </summary>
/// <remarks>
/// The end-to-end test proves a user sees new mail arrive; it cannot prove <i>which</i> event
/// delivered it, because any invalidation refreshes the list. These assert the distinction
/// §7 actually draws.
/// </remarks>
public sealed class SyncEventTests
{
	/// <summary>
	/// Backfill announces no new mail.
	/// </summary>
	/// <remarks>
	/// A new account's backlog is not news. Announcing it is the notification flood §13 Epic 9
	/// exists to prevent — worst on a triggered resync of an established mailbox, where every
	/// message the user already read would arrive again as an event.
	/// </remarks>
	[Fact]
	public async Task Backfill_announces_no_new_mail()
	{
		await using var harness = await SyncHarness.CreateAsync(ProviderShapes.Graph);
		harness.Provider.AddMailbox("INBOX", SpecialUse.Inbox);
		for (var i = 0; i < 3; i++)
		{
			harness.Provider.SeedMessage("INBOX", Guid.NewGuid(), DateTimeOffset.UnixEpoch.AddMinutes(i));
		}

		await SyncTests.ReconcileAsync(harness);
		await SyncTests.CoverAsync(harness);

		Assert.Empty(harness.Events.Received);
		Assert.Empty(harness.Events.Updated);
	}

	/// <summary>The change stream is steady state, so a message first seen there is new mail.</summary>
	[Fact]
	public async Task The_change_stream_announces_new_mail()
	{
		await using var harness = await SyncHarness.CreateAsync(ProviderShapes.Graph);
		harness.Provider.AddMailbox("INBOX", SpecialUse.Inbox);
		await SyncTests.ReconcileAsync(harness);
		await SyncTests.CoverAsync(harness);
		harness.Events.Clear();

		harness.Provider.SeedMessage("INBOX", Guid.NewGuid(), DateTimeOffset.UnixEpoch);
		await SyncTests.SyncAsync(harness);

		Assert.Single(harness.Events.Received);
	}

	/// <summary>
	/// A poll that observed nothing new announces nothing.
	/// </summary>
	/// <remarks>
	/// IMAP returns the mailbox's messages on every poll, so without comparing against what is
	/// already stored, every message would be announced as updated every minute — and an event
	/// that fires when nothing happened tells a listener nothing.
	/// </remarks>
	[Fact]
	public async Task An_idle_poll_announces_nothing()
	{
		await using var harness = await SyncHarness.CreateAsync(ProviderShapes.Graph);
		harness.Provider.AddMailbox("INBOX", SpecialUse.Inbox);
		harness.Provider.SeedMessage("INBOX", Guid.NewGuid(), DateTimeOffset.UnixEpoch);

		await SyncTests.ReconcileAsync(harness);
		await SyncTests.CoverAsync(harness);
		await SyncTests.SyncAsync(harness);
		harness.Events.Clear();

		await SyncTests.SyncAsync(harness);

		Assert.Empty(harness.Events.Received);
		Assert.Empty(harness.Events.Updated);
	}

	/// <summary>A server-side flag change is an update to announce.</summary>
	[Fact]
	public async Task A_server_side_change_is_announced_as_an_update()
	{
		await using var harness = await SyncHarness.CreateAsync(ProviderShapes.Graph);
		harness.Provider.AddMailbox("INBOX", SpecialUse.Inbox);
		var occurrenceId = harness.Provider.SeedMessage("INBOX", Guid.NewGuid(), DateTimeOffset.UnixEpoch);

		await SyncTests.ReconcileAsync(harness);
		await SyncTests.CoverAsync(harness);
		await SyncTests.SyncAsync(harness);
		harness.Events.Clear();

		var messageId = await harness.UsingAsync(async scope =>
			(
				await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.SingleAsync(
					scope.GetRequiredService<Api.Persistence.MyloMailDbContext>().Messages
				)
			).Id
		);

		await harness.UsingAsync(async scope =>
			await harness.Provider.SetFlagsAsync(
				await harness.AccountInScopeAsync(scope),
				[new Api.Providers.Contracts.MessageOccurrenceRef(messageId, Guid.Empty, occurrenceId)],
				new Api.Providers.Contracts.FlagUpdate(IsRead: true, IsFlagged: null),
				default
			)
		);

		await SyncTests.SyncAsync(harness);

		Assert.Single(harness.Events.Updated);
		Assert.Empty(harness.Events.Received);
	}
}
