using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MimeKit;
using MyloMail.Api.Compose;
using MyloMail.Api.Domain;
using MyloMail.Api.Persistence;
using MyloMail.Api.Tests.Fakes;
using MyloMail.Api.Tests.Sync;
using Xunit;

namespace MyloMail.Api.Tests.Compose;

/// <summary>
/// §1's promise that a draft push conflict "prompts resolution rather than overwriting" had
/// no way to actually resolve one — <see cref="Draft.SyncConflict"/> was set but nothing ever
/// cleared it, so a conflicted draft silently stopped syncing forever. Fifty-second
/// architecture-review pass.
/// </summary>
public sealed class DraftConflictResolutionTests
{
	[Fact]
	public async Task Resolving_a_non_conflicted_draft_is_a_safe_no_op()
	{
		await using var harness = await SyncHarness.CreateAsync(ProviderShapes.Gmail);
		var (draftId, providerDraftId) = await SeedAsync(harness, conflict: false);

		await harness.UsingAsync(async scope =>
			await scope.GetRequiredService<DraftService>().ResolveConflictAsync(draftId, keepMine: true)
		);

		await harness.UsingAsync(async scope =>
		{
			var draft = await scope.GetRequiredService<MyloMailDbContext>().Drafts.SingleAsync(d => d.Id == draftId);
			Assert.False(draft.SyncConflict);
			Assert.Equal(providerDraftId, draft.ProviderDraftId);
		});
	}

	[Fact]
	public async Task Keep_mine_abandons_the_old_remote_draft_and_lets_a_fresh_push_create_a_new_one()
	{
		await using var harness = await SyncHarness.CreateAsync(ProviderShapes.Gmail);
		var (draftId, _) = await SeedAsync(harness, conflict: true);

		await harness.UsingAsync(async scope =>
			await scope.GetRequiredService<DraftService>().ResolveConflictAsync(draftId, keepMine: true)
		);

		await harness.UsingAsync(async scope =>
		{
			var draft = await scope.GetRequiredService<MyloMailDbContext>().Drafts.SingleAsync(d => d.Id == draftId);
			Assert.False(draft.SyncConflict);
			// Abandoned, not force-updated: a null ProviderDraftId is what lets the next
			// ordinary push create a genuinely fresh remote draft rather than attempting an
			// update against a revision that is already known stale.
			Assert.Null(draft.ProviderDraftId);
			Assert.Null(draft.ProviderRevision);
		});

		// The next push picks it back up (no longer excluded by the SyncConflict filter) and
		// creates a brand-new remote draft from local content.
		var pushed = await harness.UsingAsync(async scope =>
			await scope.GetRequiredService<DraftSyncService>().PushAsync(harness.Account.Id)
		);
		Assert.Equal(1, pushed);
		Assert.Single(harness.Provider.DraftProviderIdsIssued);
	}

	[Fact]
	public async Task Keep_theirs_discards_the_local_edit_and_adopts_the_servers_current_content()
	{
		await using var harness = await SyncHarness.CreateAsync(ProviderShapes.Gmail);
		var (draftId, _) = await SeedAsync(harness, conflict: true);

		await harness.UsingAsync(async scope =>
			await scope.GetRequiredService<DraftService>().ResolveConflictAsync(draftId, keepMine: false)
		);

		await harness.UsingAsync(async scope =>
		{
			var draft = await scope.GetRequiredService<MyloMailDbContext>().Drafts.SingleAsync(d => d.Id == draftId);
			Assert.False(draft.SyncConflict);
			Assert.Equal("Server's subject", draft.Subject);
			Assert.Contains("Server's body", draft.BodyHtml);
			// Not re-pushed: the local edit was discarded, so there is nothing left to push.
			Assert.True(draft.PushedAt >= draft.SavedAt);
		});
	}

	/// <summary>
	/// Nothing enforces that at most one mailbox per account has effective
	/// <see cref="SpecialUse.Drafts"/> — a manual override has no uniqueness check against
	/// other mailboxes already holding that use. Picking an arbitrary one of several
	/// candidates would address the raw-message fetch against the wrong mailbox's id-space,
	/// silently materialising a different message's content into this draft — failing loudly
	/// is required instead.
	/// </summary>
	[Fact]
	public async Task Keep_theirs_fails_loudly_rather_than_guessing_when_two_mailboxes_are_both_drafts()
	{
		await using var harness = await SyncHarness.CreateAsync(ProviderShapes.Gmail);
		var (draftId, _) = await SeedAsync(harness, conflict: true);

		await harness.UsingAsync(async scope =>
		{
			var context = scope.GetRequiredService<MyloMailDbContext>();
			context.Mailboxes.Add(
				new Mailbox
				{
					Id = Guid.NewGuid(),
					AccountId = harness.Account.Id,
					ProviderMailboxId = "OTHER-DRAFT",
					Name = "Also Drafts",
					SpecialUse = SpecialUse.None,
					SpecialUseOverride = SpecialUse.Drafts,
				}
			);
			await context.SaveChangesAsync();
		});

		await harness.UsingAsync(async scope =>
			await Assert.ThrowsAsync<InvalidOperationException>(
				() => scope.GetRequiredService<DraftService>().ResolveConflictAsync(draftId, keepMine: false)
			)
		);
	}

	private static async Task<(Guid DraftId, string ProviderDraftId)> SeedAsync(SyncHarness harness, bool conflict)
	{
		var draftsMailbox = harness.Provider.AddMailbox("DRAFT", SpecialUse.Drafts);
		var providerDraftId = harness.Provider.SeedMessage("DRAFT", Guid.NewGuid(), DateTimeOffset.UnixEpoch);
		draftsMailbox.Messages[providerDraftId].RawBytes = ServerCopyBytes();

		var draftId = Guid.NewGuid();
		await harness.UsingAsync(async scope =>
		{
			var context = scope.GetRequiredService<MyloMailDbContext>();
			var identity = new SendIdentity
			{
				Id = Guid.NewGuid(),
				AccountId = harness.Account.Id,
				EmailAddress = "author@example.test",
				IsDefault = true,
			};
			context.SendIdentities.Add(identity);
			context.Mailboxes.Add(
				new Mailbox
				{
					Id = Guid.NewGuid(),
					AccountId = harness.Account.Id,
					ProviderMailboxId = "DRAFT",
					Name = "Drafts",
					SpecialUse = SpecialUse.Drafts,
				}
			);
			context.Drafts.Add(
				new Draft
				{
					Id = draftId,
					AccountId = harness.Account.Id,
					SendIdentityId = identity.Id,
					Subject = "My local subject",
					BodyHtml = "<p>My local body</p>",
					SavedAt = DateTimeOffset.UnixEpoch,
					PushedAt = DateTimeOffset.UnixEpoch,
					ProviderDraftId = providerDraftId,
					ProviderRevision = "rev-1",
					SyncConflict = conflict,
				}
			);
			await context.SaveChangesAsync();
		});

		return (draftId, providerDraftId);
	}

	private static byte[] ServerCopyBytes()
	{
		var message = new MimeMessage();
		message.From.Add(MailboxAddress.Parse("author@example.test"));
		message.Subject = "Server's subject";
		message.Body = new TextPart("html") { Text = "<p>Server's body</p>" };
		using var stream = new MemoryStream();
		message.WriteTo(stream);
		return stream.ToArray();
	}
}
