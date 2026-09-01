using Microsoft.AspNetCore.SignalR;
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

	[Fact]
	public async Task Reordering_siblings_sets_their_local_sort_order_and_leaves_other_parents_alone()
	{
		await using var harness = await SyncHarness.CreateAsync(ProviderShapes.Imap(ImapCapabilityTier.QResync));

		await harness.UsingAsync(async scope =>
		{
			var context = scope.GetRequiredService<MyloMailDbContext>();
			var account = await harness.AccountInScopeAsync(scope);

			Mailbox Root(string name) =>
				new()
				{
					Id = Guid.NewGuid(),
					AccountId = account.Id,
					ProviderMailboxId = name,
					Name = name,
				};

			var (a, b, c) = (Root("A"), Root("B"), Root("C"));
			var unrelated = Root("Elsewhere");
			context.Mailboxes.AddRange(a, b, c, unrelated);
			await context.SaveChangesAsync();

			await scope
				.GetRequiredService<MailboxManagement>()
				.ReorderAsync(account.Id, null, [c.Id, a.Id, b.Id]);

			var reloaded = await context.Mailboxes.ToDictionaryAsync(m => m.Name, m => m.LocalSortOrder);
			Assert.Equal(1, reloaded["A"]);
			Assert.Equal(2, reloaded["B"]);
			Assert.Equal(0, reloaded["C"]);
			// Never touched: reordering one sibling group must not renumber a folder from
			// another parent that happens to share the (default) sort order.
			Assert.Equal(0, reloaded["Elsewhere"]);
		});
	}

	[Fact]
	public async Task Moving_a_folder_into_its_own_descendant_is_rejected()
	{
		await using var harness = await SyncHarness.CreateAsync(ProviderShapes.Imap(ImapCapabilityTier.QResync));

		await harness.UsingAsync(async scope =>
		{
			var context = scope.GetRequiredService<MyloMailDbContext>();
			var account = await harness.AccountInScopeAsync(scope);

			var parent = new Mailbox { Id = Guid.NewGuid(), AccountId = account.Id, ProviderMailboxId = "Parent", Name = "Parent" };
			var child = new Mailbox { Id = Guid.NewGuid(), AccountId = account.Id, ProviderMailboxId = "Child", Name = "Child", ParentId = parent.Id };
			var grandchild = new Mailbox { Id = Guid.NewGuid(), AccountId = account.Id, ProviderMailboxId = "Grandchild", Name = "Grandchild", ParentId = child.Id };
			context.Mailboxes.AddRange(parent, child, grandchild);
			await context.SaveChangesAsync();

			var mailboxes = scope.GetRequiredService<MailboxManagement>();

			// Every depth: onto itself, onto a direct child, onto a deeper descendant.
			await Assert.ThrowsAsync<HubException>(() => mailboxes.MoveAsync(parent.Id, parent.Id));
			await Assert.ThrowsAsync<HubException>(() => mailboxes.MoveAsync(parent.Id, child.Id));
			await Assert.ThrowsAsync<HubException>(() => mailboxes.MoveAsync(parent.Id, grandchild.Id));

			// Untouched by the rejected attempts.
			var reloaded = await context.Mailboxes.FirstAsync(m => m.Id == parent.Id);
			Assert.Null(reloaded.ParentId);
		});
	}
}
