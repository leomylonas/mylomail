using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MyloMail.Api.Domain;
using MyloMail.Api.Persistence;
using MyloMail.Api.Providers;
using MyloMail.Api.Tests.Fakes;
using Xunit;

namespace MyloMail.Api.Tests.Sync;

public sealed class UnreadConfirmedDeltaTests
{
	/// <summary>
	/// The provider's own reading already includes what was confirmed before it was taken, so a topology
	/// pass takes the provider's figure as it stands and clears the delta kept beside it.
	/// </summary>
	[Fact]
	public async Task A_new_provider_reading_replaces_the_snapshot_and_clears_the_confirmed_delta()
	{
		await using var harness = await SyncHarness.CreateAsync(ProviderShapes.Imap(ImapCapabilityTier.QResync));
		harness.Provider.AddMailbox("INBOX", SpecialUse.Inbox);
		await SyncTests.ReconcileAsync(harness);
		await harness.UsingAsync(async scope =>
		{
			var context = scope.GetRequiredService<MyloMailDbContext>();
			(await context.Mailboxes.SingleAsync(m => m.ProviderMailboxId == "INBOX")).UnreadConfirmedDelta = -3;
			await context.SaveChangesAsync();
		});

		await SyncTests.ReconcileAsync(harness);

		await harness.UsingAsync(async scope =>
			Assert.Equal(0, (await scope.GetRequiredService<MyloMailDbContext>().Mailboxes.SingleAsync(m => m.ProviderMailboxId == "INBOX")).UnreadConfirmedDelta)
		);
	}
}
