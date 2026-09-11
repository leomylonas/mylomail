using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MyloMail.Api.Domain;
using MyloMail.Api.Persistence;
using MyloMail.Api.Providers;
using MyloMail.Api.Tests.Fakes;
using MyloMail.Api.Tests.Persistence;
using Xunit;

namespace MyloMail.Api.Tests.Sync;

/// <summary>
/// User-approved feature, following a forty-third-pass finding: a manual correction of a
/// mailbox's role (§13 Epic 2), highest precedence over both a real server-reported RFC 6154
/// SPECIAL-USE attribute and the provider's own name-based fallback guess.
/// </summary>
public sealed class MailboxSpecialUseOverrideTests
{
	[Theory]
	[InlineData(null, SpecialUse.Sent, SpecialUse.Sent)]
	[InlineData(SpecialUse.Trash, SpecialUse.Sent, SpecialUse.Trash)]
	[InlineData(SpecialUse.None, SpecialUse.Sent, SpecialUse.None)]
	public void The_override_takes_precedence_when_set(
		SpecialUse? overrideValue,
		SpecialUse synced,
		SpecialUse expected
	)
	{
		var mailbox = new Mailbox { SpecialUse = synced, SpecialUseOverride = overrideValue };
		Assert.Equal(expected, mailbox.EffectiveSpecialUse);
	}

	[Fact]
	public async Task Special_mailbox_resolution_requires_one_effective_role()
	{
		await using var database = new TestDatabase();
		await database.MigrateAsync();
		await using var scope = database.CreateScope();
		var context = scope.ServiceProvider.GetRequiredService<MyloMailDbContext>();
		var accountId = Guid.NewGuid();
		var primary = new Mailbox
		{
			Id = Guid.NewGuid(),
			AccountId = accountId,
			Name = "Trash",
			ProviderMailboxId = "Trash",
			SpecialUse = SpecialUse.Trash,
		};
		context.Accounts.Add(
			new Account
			{
				Id = accountId,
				DisplayName = "IMAP",
				ProviderType = ProviderType.Imap,
			}
		);
		context.Mailboxes.Add(primary);
		await context.SaveChangesAsync();

		var resolver = new DbProviderMailboxResolver(context);
		Assert.Equal(primary.Id, resolver.SpecialMailboxId(accountId, SpecialUse.Trash));

		context.Mailboxes.Add(
			new Mailbox
			{
				Id = Guid.NewGuid(),
				AccountId = accountId,
				Name = "Bin",
				ProviderMailboxId = "Bin",
				SpecialUse = SpecialUse.None,
				SpecialUseOverride = SpecialUse.Trash,
			}
		);
		await context.SaveChangesAsync();

		Assert.Null(resolver.SpecialMailboxId(accountId, SpecialUse.Trash));
	}

	/// <summary>
	/// The override column is never written by topology reconciliation (`TopologySyncService`'s
	/// `Create`/`Update` only ever touch `SpecialUse`) — a resync must not silently clobber a
	/// user's correction.
	/// </summary>
	[Fact]
	public async Task A_resync_does_not_clobber_a_set_override()
	{
		await using var harness = await SyncHarness.CreateAsync(ProviderShapes.Graph);
		harness.Provider.AddMailbox("INBOX", SpecialUse.Inbox);
		harness.Provider.AddMailbox("Projects", SpecialUse.None);
		await SyncTests.ReconcileAsync(harness);

		await harness.UsingAsync(async scope =>
		{
			var context = scope.GetRequiredService<MyloMailDbContext>();
			var mailbox = await context.Mailboxes.SingleAsync(m => m.ProviderMailboxId == "Projects");
			mailbox.SpecialUseOverride = SpecialUse.Archive;
			await context.SaveChangesAsync();
		});

		// Simulates a resync observing the same folder again — Update() runs, refreshing
		// SpecialUse from the provider, but never touching SpecialUseOverride.
		await SyncTests.ReconcileAsync(harness);

		await harness.UsingAsync(async scope =>
		{
			var context = scope.GetRequiredService<MyloMailDbContext>();
			var mailbox = await context.Mailboxes.SingleAsync(m => m.ProviderMailboxId == "Projects");
			Assert.Equal(SpecialUse.Archive, mailbox.SpecialUseOverride);
			Assert.Equal(SpecialUse.Archive, mailbox.EffectiveSpecialUse);
		});
	}
}
