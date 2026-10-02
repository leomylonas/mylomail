using MyloMail.Api.Domain;
using MyloMail.Api.Tests.Fakes;
using Xunit;

namespace MyloMail.Api.Tests.Sync;

/// <summary>
/// The sidebar shows the provider's own counts, refreshed by topology reconciliation. When a
/// count moves the client has to be told, or it keeps showing the old number after the message
/// has been read.
/// </summary>
public sealed class MailboxCountAnnouncementTests
{
	[Fact]
	public async Task A_changed_unread_count_is_announced_for_that_mailbox_only()
	{
		await using var harness = await SyncHarness.CreateAsync(ProviderShapes.Graph);
		var inbox = harness.Provider.AddMailbox("INBOX", SpecialUse.Inbox);
		harness.Provider.AddMailbox("Projects", SpecialUse.None);
		var occurrence = harness.Provider.SeedMessage("INBOX", Guid.NewGuid(), DateTimeOffset.UnixEpoch);
		await SyncTests.ReconcileAsync(harness);
		harness.Events.Clear();

		inbox.Messages[occurrence].IsRead = true;
		await SyncTests.ReconcileAsync(harness);

		var announced = Assert.Single(harness.Events.Mailboxes);
		Assert.Equal("INBOX", announced.Name);
		Assert.Equal(0, announced.ProviderUnreadCount);
	}

	[Fact]
	public async Task A_poll_that_changes_no_counts_announces_nothing()
	{
		await using var harness = await SyncHarness.CreateAsync(ProviderShapes.Graph);
		harness.Provider.AddMailbox("INBOX", SpecialUse.Inbox);
		harness.Provider.SeedMessage("INBOX", Guid.NewGuid(), DateTimeOffset.UnixEpoch);
		await SyncTests.ReconcileAsync(harness);
		harness.Events.Clear();

		await SyncTests.ReconcileAsync(harness);

		Assert.Empty(harness.Events.Mailboxes);
	}
}
