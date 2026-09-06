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

	/// <summary>
	/// Sixty-fifth pass: a provider rejection (a duplicate name, a namespace the server won't
	/// accept) from Create/Rename/Move/Delete was left to propagate raw, so SignalR's default
	/// "An unexpected error occurred" (detailed errors are off) reached the user instead of the
	/// provider's real message — silently defeating MailboxTree.tsx's error banner, which exists
	/// specifically to show why the operation was rejected.
	/// </summary>
	[Fact]
	public async Task A_provider_rejection_reaches_the_caller_as_a_HubException_with_the_real_message()
	{
		await using var harness = await SyncHarness.CreateAsync(ProviderShapes.Imap(ImapCapabilityTier.QResync));
		var mailbox = harness.Provider.AddMailbox("Existing", SpecialUse.None);
		await SyncTests.ReconcileAsync(harness);

		await harness.UsingAsync(async scope =>
		{
			var context = scope.GetRequiredService<MyloMailDbContext>();
			var account = await harness.AccountInScopeAsync(scope);
			var local = await context.Mailboxes.FirstAsync(m => m.ProviderMailboxId == "Existing");
			var mailboxes = scope.GetRequiredService<MailboxManagement>();

			harness.Provider.FailMailboxOperationWith(new InvalidOperationException("Mailbox already exists."));
			var ex = await Assert.ThrowsAsync<HubException>(
				() => mailboxes.RenameAsync(local.Id, "AlsoExisting")
			);
			Assert.Equal("Mailbox already exists.", ex.Message);
		});
	}

	/// <summary>
	/// Two-hundred-and-eighteenth pass: <c>FolderNameModal.tsx</c> trims its input and disables
	/// its own submit on a blank result, but that is a client-side convenience only — a direct
	/// hub call bypassing it (or a race with the not-yet-initialised form) previously reached the
	/// provider with a blank or whitespace-only name and failed with a raw, provider-specific
	/// error instead of the same clean rejection every other invalid input on this path gets.
	/// </summary>
	[Fact]
	public async Task A_blank_or_whitespace_folder_name_is_rejected_on_create_and_rename()
	{
		await using var harness = await SyncHarness.CreateAsync(ProviderShapes.Imap(ImapCapabilityTier.QResync));
		harness.Provider.AddMailbox("INBOX", SpecialUse.Inbox);
		await SyncTests.ReconcileAsync(harness);

		await harness.UsingAsync(async scope =>
		{
			var context = scope.GetRequiredService<MyloMailDbContext>();
			var account = await harness.AccountInScopeAsync(scope);
			var mailboxes = scope.GetRequiredService<MailboxManagement>();

			await Assert.ThrowsAsync<HubException>(() => mailboxes.CreateAsync(account.Id, "", null));
			await Assert.ThrowsAsync<HubException>(() => mailboxes.CreateAsync(account.Id, "   ", null));

			var inbox = await context.Mailboxes.FirstAsync(m => m.ProviderMailboxId == "INBOX");
			await Assert.ThrowsAsync<HubException>(() => mailboxes.RenameAsync(inbox.Id, ""));
			await Assert.ThrowsAsync<HubException>(() => mailboxes.RenameAsync(inbox.Id, "   "));

			// Neither rejected attempt reached the provider or changed anything locally.
			Assert.DoesNotContain(
				await context.Mailboxes.Select(m => m.Name).ToListAsync(),
				name => string.IsNullOrWhiteSpace(name)
			);
		});
	}

	/// <summary>
	/// Seventy-first pass: unlike the cycle guard right above, nothing checked that a
	/// client-supplied parent id actually belongs to the same account before <c>Move</c>/
	/// <c>Create</c> carried it through reconciliation into a persisted
	/// <see cref="Mailbox.ParentId"/> — corrupting the per-account tree every reader assumes it
	/// can walk within one account's rows alone.
	/// </summary>
	[Fact]
	public async Task Moving_a_folder_under_another_accounts_folder_is_rejected()
	{
		await using var harness = await SyncHarness.CreateAsync(ProviderShapes.Imap(ImapCapabilityTier.QResync));

		await harness.UsingAsync(async scope =>
		{
			var context = scope.GetRequiredService<MyloMailDbContext>();
			var account = await harness.AccountInScopeAsync(scope);

			var otherAccount = new Account
			{
				Id = Guid.NewGuid(),
				DisplayName = "Other",
				ProviderType = ProviderType.Imap,
				InitialSyncMode = InitialSyncMode.Full,
			};
			var foreignParent = new Mailbox
			{
				Id = Guid.NewGuid(),
				AccountId = otherAccount.Id,
				ProviderMailboxId = "Foreign",
				Name = "Foreign",
			};
			var mine = new Mailbox
			{
				Id = Guid.NewGuid(),
				AccountId = account.Id,
				ProviderMailboxId = "Mine",
				Name = "Mine",
			};
			context.Accounts.Add(otherAccount);
			context.Mailboxes.AddRange(foreignParent, mine);
			await context.SaveChangesAsync();

			var mailboxes = scope.GetRequiredService<MailboxManagement>();

			await Assert.ThrowsAsync<HubException>(() => mailboxes.MoveAsync(mine.Id, foreignParent.Id));
			await Assert.ThrowsAsync<HubException>(() => mailboxes.CreateAsync(account.Id, "New", foreignParent.Id));

			var reloaded = await context.Mailboxes.FirstAsync(m => m.Id == mine.Id);
			Assert.Null(reloaded.ParentId);
		});
	}

	/// <summary>
	/// Two-hundred-and-thirty-fourth pass: pass 233 disabled the "Move to"/"Move to top level"
	/// keyboard actions and the drag reparent for a synthesized mailbox row (no real provider
	/// id — a local nested Gmail-label-group intermediate), but only in the renderer. A direct
	/// Rename/Move/Delete hub call for one still reached the provider's own internal
	/// "always have a provider id" assertion, surfaced as a confusing HubException rather than
	/// the clean guidance the renderer already gives for this exact row shape.
	/// </summary>
	[Fact]
	public async Task Renaming_moving_or_deleting_a_synthesized_mailbox_is_rejected_with_clear_guidance()
	{
		await using var harness = await SyncHarness.CreateAsync(ProviderShapes.Imap(ImapCapabilityTier.QResync));

		await harness.UsingAsync(async scope =>
		{
			var context = scope.GetRequiredService<MyloMailDbContext>();
			var account = await harness.AccountInScopeAsync(scope);

			var synthesized = new Mailbox
			{
				Id = Guid.NewGuid(),
				AccountId = account.Id,
				ProviderMailboxId = null,
				Name = "Projects",
			};
			var otherParent = new Mailbox
			{
				Id = Guid.NewGuid(),
				AccountId = account.Id,
				ProviderMailboxId = "OtherParent",
				Name = "OtherParent",
			};
			context.Mailboxes.AddRange(synthesized, otherParent);
			await context.SaveChangesAsync();

			var mailboxes = scope.GetRequiredService<MailboxManagement>();

			var renameEx = await Assert.ThrowsAsync<HubException>(
				() => mailboxes.RenameAsync(synthesized.Id, "Renamed")
			);
			Assert.Equal(
				"Gmail doesn't support renaming a nested label group directly — rename the label itself in Gmail.",
				renameEx.Message
			);

			var moveEx = await Assert.ThrowsAsync<HubException>(
				() => mailboxes.MoveAsync(synthesized.Id, otherParent.Id)
			);
			Assert.Equal(
				"Gmail doesn't support moving a nested label group directly — move the label itself in Gmail.",
				moveEx.Message
			);

			var deleteEx = await Assert.ThrowsAsync<HubException>(() => mailboxes.DeleteAsync(synthesized.Id));
			Assert.Equal(
				"Gmail doesn't support deleting a nested label group directly — delete the label itself in Gmail.",
				deleteEx.Message
			);

			// None of the rejected calls reached the provider or changed anything locally.
			var reloaded = await context.Mailboxes.FirstAsync(m => m.Id == synthesized.Id);
			Assert.Equal("Projects", reloaded.Name);
			Assert.Null(reloaded.ParentId);
		});
	}
}
