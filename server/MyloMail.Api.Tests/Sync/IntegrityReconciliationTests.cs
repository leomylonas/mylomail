using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MyloMail.Api.Domain;
using MyloMail.Api.Persistence;
using MyloMail.Api.Providers;
using MyloMail.Api.Providers.Contracts;
using MyloMail.Api.Sync;
using MyloMail.Api.Tests.Fakes;
using Xunit;

namespace MyloMail.Api.Tests.Sync;

/// <summary>Routine degraded-IMAP maintenance, distinct from cursor-invalid resync.</summary>
public sealed class IntegrityReconciliationTests
{
	[Fact]
	public void Periodic_reconciliation_is_not_required_when_the_cursor_reports_every_fact()
	{
		Assert.False(IntegrityReconciliationService.Required(ProviderShapes.Graph));
		Assert.False(IntegrityReconciliationService.Required(ProviderShapes.Imap(ImapCapabilityTier.QResync)));
	}

	[Fact]
	public async Task Condstore_uid_set_reconciliation_removes_an_expunge_despite_a_valid_cursor()
	{
		await using var harness = await SyncHarness.CreateAsync(ProviderShapes.Imap(ImapCapabilityTier.CondStore));
		harness.Provider.AddMailbox("INBOX", SpecialUse.Inbox);
		var serverOccurrence = harness.Provider.SeedMessage("INBOX", Guid.NewGuid(), DateTimeOffset.UnixEpoch);
		await SyncTests.ReconcileAsync(harness);
		await SyncTests.CoverAsync(harness);

		harness.Provider.RemoveMessage(serverOccurrence);
		await harness.UsingAsync(async services =>
		{
			var context = services.GetRequiredService<MyloMailDbContext>();
			var account = await harness.AccountInScopeAsync(services);
			var mailbox = await context.Mailboxes.SingleAsync();
			await services.GetRequiredService<IntegrityReconciliationService>().ReconcileAsync(account, mailbox);
		});

		var messageId = await harness.UsingAsync(async services =>
			(await services.GetRequiredService<MyloMailDbContext>().Messages.SingleAsync()).Id
		);
		await harness.UsingAsync(async services =>
		{
			var context = services.GetRequiredService<MyloMailDbContext>();
			Assert.Empty(await context.MessageMailboxes.ToListAsync());
			Assert.NotNull((await context.IntegrityReconciliationStates.SingleAsync()).LastReconciledAt);
		});
		Assert.Equal(messageId, Assert.Single(harness.Events.Deleted));
	}

	[Fact]
	public async Task Basic_imap_periodically_scans_flags_when_the_cursor_cannot_express_them()
	{
		await using var harness = await SyncHarness.CreateAsync(ProviderShapes.Imap(ImapCapabilityTier.Basic));
		harness.Provider.AddMailbox("INBOX", SpecialUse.Inbox);
		harness.Provider.SeedMessage("INBOX", Guid.NewGuid(), DateTimeOffset.UnixEpoch);
		await SyncTests.ReconcileAsync(harness);
		await SyncTests.CoverAsync(harness);

		await harness.UsingAsync(async services =>
		{
			var context = services.GetRequiredService<MyloMailDbContext>();
			var account = await harness.AccountInScopeAsync(services);
			var mailbox = await context.Mailboxes.SingleAsync();
			var occurrence = await context.MessageMailboxes.SingleAsync();
			await harness.Provider.SetFlagsAsync(account, [new MessageOccurrenceRef(occurrence.MessageId, mailbox.Id, occurrence.ProviderOccurrenceId)], new FlagUpdate(true, null), default);
			await services.GetRequiredService<IntegrityReconciliationService>().ReconcileAsync(account, mailbox);
			Assert.True((await context.Messages.SingleAsync()).IsRead);
		});
	}
}
