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
/// Folder lifecycle (§2). Each operation reconciles topology afterwards rather than editing
/// the local row, because the server decides what a folder ends up called.
/// </summary>
public class MailboxManagementTests
{
	[Fact]
	public async Task A_created_folder_appears_in_the_local_tree()
	{
		await using var harness = await SyncHarness.CreateAsync(ProviderShapes.Imap(ImapCapabilityTier.QResync));
		harness.Provider.AddMailbox("INBOX", SpecialUse.Inbox);
		await SyncTests.ReconcileAsync(harness);

		await harness.UsingAsync(async scope =>
		{
			var context = scope.GetRequiredService<MyloMailDbContext>();
			var account = await harness.AccountInScopeAsync(scope);

			await scope.GetRequiredService<MailboxManagement>().CreateAsync(account.Id, "Receipts", null);

			Assert.Contains(
				await context.Mailboxes.Select(m => m.Name).ToListAsync(),
				name => name == "Receipts"
			);
		});
	}
}
