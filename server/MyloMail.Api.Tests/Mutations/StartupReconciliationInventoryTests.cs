using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MyloMail.Api.Domain;
using MyloMail.Api.Mutations;
using MyloMail.Api.Persistence;
using MyloMail.Api.Tests.Mutations;
using Xunit;

namespace MyloMail.Api.Tests.Mutations;

/// <summary>
/// Startup reconciliation is the sole recovery mechanism with in-memory job storage. Every
/// durable work source, including exports and notifications, must therefore be visible in its
/// single inventory rather than rediscovered by unrelated startup code.
/// </summary>
public sealed class StartupReconciliationInventoryTests
{
	[Fact]
	public async Task Finds_pending_drafts_exports_and_undelivered_notifications()
	{
		await using var harness = await MutationHarness.CreateAsync();
		var exportId = Guid.NewGuid();

		await harness.UsingAsync(async services =>
		{
			var context = services.GetRequiredService<MyloMailDbContext>();
			var identityId = Guid.NewGuid();
			context.SendIdentities.Add(new SendIdentity
			{
				Id = identityId,
				AccountId = harness.AccountId,
				EmailAddress = "author@example.test",
				IsDefault = true,
			});
			context.Drafts.Add(new Draft
			{
				Id = Guid.NewGuid(),
				AccountId = harness.AccountId,
				SendIdentityId = identityId,
				SavedAt = DateTimeOffset.UnixEpoch,
			});
			context.ExportJobs.Add(new ExportJob
			{
				Id = exportId,
				AccountId = harness.AccountId,
				DestinationPath = "/tmp/export",
				Status = ExportJobStatus.CancelRequested,
				CreatedAt = DateTimeOffset.UnixEpoch,
			});
			context.NotificationRecords.Add(new NotificationRecord
			{
				Id = Guid.NewGuid(),
				AccountId = harness.AccountId,
				MessageId = harness.MessageId,
				Kind = NotificationKind.NewMessage,
				CreatedAt = DateTimeOffset.UnixEpoch,
			});
			await context.SaveChangesAsync();
		});
		var work = await harness.UsingAsync(services =>
			services.GetRequiredService<StartupReconciliation>().FindAsync()
		);

		Assert.Equal([exportId], work.IncompleteExports);
		Assert.Equal([harness.AccountId], work.DraftAccountsNeedingPush);
		Assert.Equal([harness.AccountId], work.UndeliveredNotificationAccounts);
	}
}
