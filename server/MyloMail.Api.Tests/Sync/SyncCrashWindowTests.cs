using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MimeKit;
using MyloMail.Api.Domain;
using MyloMail.Api.FaultInjection;
using MyloMail.Api.Persistence;
using MyloMail.Api.Providers;
using MyloMail.Api.Sync;
using MyloMail.Api.Tests.Fakes;
using Xunit;

namespace MyloMail.Api.Tests.Sync;

/// <summary>
/// The cursor and page boundaries from §16's table.
/// </summary>
/// <remarks>
/// The rule under test throughout: never persist a cursor past changes that have not been
/// durably persisted. Replay is acceptable and skipping is not, so every assertion here is
/// about the page being replayed rather than about it being applied exactly once.
/// </remarks>
[Trait("Category", "FaultInjection")]
[Trait("Category", "Deep")]
public sealed class SyncCrashWindowTests
{
	/// <summary>
	/// A staged Gmail page has already advanced the account cursor. Its raw draft bytes must
	/// therefore survive until replay: fetching them from the server later would silently lose
	/// a draft deleted after the history page was read.
	/// </summary>
	[Fact]
	public async Task A_staged_remote_draft_survives_server_deletion_and_crash_before_replay()
	{
		await using var harness = await SyncHarness.CreateAsync(ProviderShapes.Gmail);
		harness.Provider.AddMailbox("INBOX", SpecialUse.Inbox);
		var drafts = harness.Provider.AddMailbox("DRAFT", SpecialUse.Drafts);
		var occurrence = harness.Provider.SeedMessage("DRAFT", Guid.NewGuid(), DateTimeOffset.UnixEpoch);
		drafts.Messages[occurrence].RawBytes = DraftMimeBytes();
		harness.Provider.SeedDraftContainer(occurrence);
		await SyncTests.ReconcileAsync(harness);

		await harness.UsingAsync(async scope =>
		{
			var context = scope.GetRequiredService<MyloMailDbContext>();
			context.SendIdentities.Add(
				new SendIdentity
				{
					Id = Guid.NewGuid(),
					AccountId = harness.Account.Id,
					EmailAddress = "author@example.test",
					IsDefault = true,
				}
			);
			await context.SaveChangesAsync();
		});

		Assert.True((await SyncTests.SyncAsync(harness, "DRAFT")).Staged);
		harness.Provider.RemoveMessage(occurrence);
		await SyncTests.CoverAsync(harness, "INBOX");
		await SyncTests.CoverAsync(harness, "DRAFT");

		harness.Faults.ArmAt(FaultPoints.SyncBeforeStagedReplay);
		await Assert.ThrowsAsync<SimulatedCrashException>(() => ReplayAsync(harness));
		await harness.RestartAsync();

		await ReplayAsync(harness);
		await harness.UsingAsync(async scope =>
		{
			var draft = await scope.GetRequiredService<MyloMailDbContext>().Drafts.SingleAsync();
			Assert.Equal("Staged remote draft", draft.Subject);
			Assert.Equal("<p>Captured before deletion</p>", draft.BodyHtml.Trim());
		});
	}

	/// <summary>Kill point: mid page, before the page and its cursor commit.</summary>
	/// <remarks>
	/// <para>
	/// Nothing was committed, so the page must be replayed in full. Skipping it would be
	/// silent and permanent — nothing later would ever mention those messages again.
	/// </para>
	/// <para>
	/// The walk is deliberately multi-page, and the crash lands on the first of them. A
	/// single-page walk cannot observe this failure at all: its resume token is null either
	/// way, so a token committed ahead of its data would look identical to one committed with
	/// it, and the test would pass against the very bug it exists to catch.
	/// </para>
	/// </remarks>
	[Fact]
	public async Task A_crash_before_the_cursor_commits_replays_the_page()
	{
		await using var harness = await SyncHarness.CreateAsync(ProviderShapes.Graph);
		harness.Provider.AddMailbox("INBOX", SpecialUse.Inbox);
		await SyncTests.ReconcileAsync(harness);

		for (var i = 0; i < 5; i++)
		{
			harness.Provider.SeedMessage("INBOX", Guid.NewGuid(), DateTimeOffset.UnixEpoch.AddMinutes(i));
		}

		harness.Faults.ArmAt(FaultPoints.SyncPageBeforeCommit);
		await Assert.ThrowsAsync<SimulatedCrashException>(() => SyncTests.CoverAsync(harness, pageSize: 2));
		await harness.RestartAsync();

		await harness.UsingAsync(async scope =>
		{
			var context = scope.GetRequiredService<MyloMailDbContext>();

			// Neither the messages nor a resume token survived, so the walk restarts from the
			// beginning rather than from a position covering data that was never written.
			Assert.Empty(await context.Messages.ToListAsync());
			var coverage = await context.MailboxCoverageStates.SingleOrDefaultAsync();
			Assert.True(coverage is null || coverage.ResumeToken is null);
		});

		await SyncTests.CoverAsync(harness, pageSize: 2);

		await harness.UsingAsync(async scope =>
		{
			var context = scope.GetRequiredService<MyloMailDbContext>();

			// Every message, including the first page's. A cursor committed ahead of its data
			// loses exactly that page, and nothing ever reports it again.
			Assert.Equal(5, await context.Messages.CountAsync());
		});
	}

	/// <summary>
	/// Kill point: after a page and its cursor commit. The replay of an already-applied page
	/// must converge, because that is the cost the never-skip rule is paid for with.
	/// </summary>
	[Fact]
	public async Task A_crash_after_the_cursor_commits_does_not_duplicate_the_page()
	{
		await using var harness = await SyncHarness.CreateAsync(ProviderShapes.Graph);
		harness.Provider.AddMailbox("INBOX", SpecialUse.Inbox);
		await SyncTests.ReconcileAsync(harness);
		harness.Provider.SeedMessage("INBOX", Guid.NewGuid(), DateTimeOffset.UnixEpoch);

		harness.Faults.ArmAt(FaultPoints.SyncPageAfterCommit);
		await Assert.ThrowsAsync<SimulatedCrashException>(() => SyncTests.CoverAsync(harness));
		await harness.RestartAsync();

		await SyncTests.CoverAsync(harness);
		await SyncTests.SyncAsync(harness);

		await harness.UsingAsync(async scope =>
		{
			var context = scope.GetRequiredService<MyloMailDbContext>();
			Assert.Equal(1, await context.Messages.CountAsync());
			Assert.Equal(1, await context.MessageMailboxes.CountAsync());
		});
	}

	/// <summary>
	/// Kill point: Gmail staged history drained, coverage complete, before canonical replay.
	/// The staged events must survive — losing them loses every change observed during the
	/// backfill, and the cursor has already moved past them.
	/// </summary>
	[Fact]
	public async Task Staged_history_survives_a_crash_before_replay()
	{
		await using var harness = await SyncHarness.CreateAsync(ProviderShapes.Gmail);
		harness.Provider.AddMailbox("INBOX", SpecialUse.Inbox);
		await SyncTests.ReconcileAsync(harness);

		harness.Provider.SeedMessage("INBOX", Guid.NewGuid(), DateTimeOffset.UnixEpoch);
		await SyncTests.SyncAsync(harness);
		await SyncTests.CoverAsync(harness);

		harness.Faults.ArmAt(FaultPoints.SyncBeforeStagedReplay);
		await Assert.ThrowsAsync<SimulatedCrashException>(() => ReplayAsync(harness));
		await harness.RestartAsync();

		await harness.UsingAsync(async scope =>
		{
			var context = scope.GetRequiredService<MyloMailDbContext>();
			Assert.NotEmpty(await context.StagedChangeEvents.ToListAsync());
		});

		Assert.True(await ReplayAsync(harness) > 0);

		await harness.UsingAsync(async scope =>
		{
			var context = scope.GetRequiredService<MyloMailDbContext>();
			Assert.Empty(await context.StagedChangeEvents.ToListAsync());
			Assert.NotEmpty(await context.Messages.ToListAsync());
		});
	}

	/// <summary>
	/// A mailbox deleted and recreated mid-sync gets a new topology generation, and a page
	/// still in flight from the previous incarnation is discarded rather than resurrecting
	/// state belonging to a mailbox that no longer exists.
	/// </summary>
	[Fact]
	public async Task A_late_page_is_discarded_on_topology_generation_mismatch()
	{
		await using var harness = await SyncHarness.CreateAsync(ProviderShapes.Graph);
		harness.Provider.AddMailbox("INBOX", SpecialUse.Inbox);
		await SyncTests.ReconcileAsync(harness);
		harness.Provider.SeedMessage("INBOX", Guid.NewGuid(), DateTimeOffset.UnixEpoch);

		var stale = await IssuedGenerationAsync(harness);
		await BumpAsync(harness);

		await harness.UsingAsync(async scope =>
		{
			var context = scope.GetRequiredService<MyloMailDbContext>();
			var mailbox = await harness.MailboxAsync(scope, "INBOX");
			var account = await harness.AccountInScopeAsync(scope);

			var page = await harness.Provider.InitialSyncMailboxAsync(
				account,
				mailbox,
				null,
				InitialSyncMode.Full,
				null,
				200,
				default
			);

			await scope
				.GetRequiredService<MessageIngestor>()
				.IngestAsync(account, page.Messages, new Dictionary<string, Mailbox> { ["INBOX"] = mailbox }, stale);
			await context.SaveChangesAsync();

			// The membership under the retired generation is not written.
			Assert.Empty(await context.MessageMailboxes.ToListAsync());
		});
	}

	/// <summary>
	/// The same protection on the removal path. A stale removal is the more dangerous of the
	/// two: provider occurrence ids repeat across incarnations, so it would delete a real
	/// occurrence belonging to the new mailbox and say nothing about it.
	/// </summary>
	[Fact]
	public async Task A_late_removal_cannot_delete_an_occurrence_of_the_new_incarnation()
	{
		await using var harness = await SyncHarness.CreateAsync(ProviderShapes.Graph);
		harness.Provider.AddMailbox("INBOX", SpecialUse.Inbox);
		await SyncTests.ReconcileAsync(harness);
		harness.Provider.SeedMessage("INBOX", Guid.NewGuid(), DateTimeOffset.UnixEpoch);
		await SyncTests.CoverAsync(harness);

		var stale = await IssuedGenerationAsync(harness);
		await BumpAsync(harness);

		var occurrenceId = await harness.UsingAsync(async scope =>
			(await scope.GetRequiredService<MyloMailDbContext>().MessageMailboxes.SingleAsync()).ProviderOccurrenceId
		);

		await harness.UsingAsync(async scope =>
		{
			var context = scope.GetRequiredService<MyloMailDbContext>();
			var mailbox = await harness.MailboxAsync(scope, "INBOX");

			await scope.GetRequiredService<MessageIngestor>().RemoveOccurrencesAsync(mailbox, [occurrenceId], stale);
			await context.SaveChangesAsync();

			Assert.Single(await context.MessageMailboxes.ToListAsync());
		});
	}

	/// <summary>
	/// And on replay. Staged events carry the generations they were observed under, because
	/// replay happens after coverage — potentially hours later, by which time a mailbox may
	/// have been replaced.
	/// </summary>
	[Fact]
	public async Task Staged_events_are_discarded_when_their_mailbox_has_been_replaced()
	{
		await using var harness = await SyncHarness.CreateAsync(ProviderShapes.Gmail);
		harness.Provider.AddMailbox("INBOX", SpecialUse.Inbox);
		await SyncTests.ReconcileAsync(harness);

		var occurrenceId = harness.Provider.SeedMessage("INBOX", Guid.NewGuid(), DateTimeOffset.UnixEpoch);
		Assert.True((await SyncTests.SyncAsync(harness)).Staged);

		// Removed from the server before coverage runs, so the membership under test can only
		// come from the staged page rather than from backfill having seen it too.
		harness.Provider.RemoveMessage(occurrenceId);
		await SyncTests.CoverAsync(harness);
		await BumpAsync(harness);
		await SyncTests.CoverAsync(harness);

		Assert.True(await ReplayAsync(harness) > 0);

		await harness.UsingAsync(async scope =>
		{
			var context = scope.GetRequiredService<MyloMailDbContext>();

			// The staged page is consumed, and the canonical message is still created — it is
			// account-scoped and belongs to no particular mailbox incarnation. What is
			// withheld is the membership, which does.
			Assert.Empty(await context.StagedChangeEvents.ToListAsync());
			Assert.NotEmpty(await context.Messages.ToListAsync());
			Assert.Empty(await context.MessageMailboxes.ToListAsync());
		});
	}

	private static Task<GenerationSnapshot> IssuedGenerationAsync(SyncHarness harness) =>
		harness.UsingAsync(async scope =>
			GenerationSnapshot.Capture([await harness.MailboxAsync(scope, "INBOX")])
		);

	private static Task BumpAsync(SyncHarness harness) =>
		harness.UsingAsync(async scope =>
			await scope
				.GetRequiredService<TopologySyncService>()
				.BumpGenerationAsync((await harness.MailboxAsync(scope, "INBOX")).Id)
		);

	private static Task<int> ReplayAsync(SyncHarness harness) =>
		harness.UsingAsync(async scope =>
			await scope
				.GetRequiredService<ChangeStreamService>()
				.ReplayStagedAsync(await harness.AccountInScopeAsync(scope))
		);

	private static byte[] DraftMimeBytes()
	{
		var message = new MimeMessage();
		message.From.Add(MailboxAddress.Parse("author@example.test"));
		message.Subject = "Staged remote draft";
		message.Body = new TextPart("html") { Text = "<p>Captured before deletion</p>" };
		using var stream = new MemoryStream();
		message.WriteTo(stream);
		return stream.ToArray();
	}
}
