using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MyloMail.Api.Domain;
using MyloMail.Api.Hubs;
using MyloMail.Api.Persistence;
using MyloMail.Api.Providers.Contracts;
using MyloMail.Api.Sync;
using MyloMail.Api.Tests.Fakes;
using Xunit;

namespace MyloMail.Api.Tests.Sync;

/// <summary>
/// Gmail's backfill is one account-wide walk (§3): every message fetched once, its label set
/// mapped onto the account's mailboxes, one cursor, and each mailbox's coverage following it.
/// </summary>
public sealed class AccountCoverageWalkTests
{
	[Fact]
	public async Task A_message_wearing_several_labels_is_stored_once_with_a_membership_for_each_mapped_label()
	{
		await using var harness = await SyncHarness.CreateAsync(ProviderShapes.Gmail);
		harness.Provider.AddMailbox("INBOX", SpecialUse.Inbox);
		harness.Provider.AddMailbox("IMPORTANT", SpecialUse.Archive);
		harness.Provider.AddMailbox("RECEIPTS");
		var wearsThree = Guid.NewGuid();
		foreach (var label in new[] { "INBOX", "IMPORTANT", "RECEIPTS" })
		{
			harness.Provider.SeedMessage(label, wearsThree, DateTimeOffset.UnixEpoch);
		}
		harness.Provider.SeedMessage("INBOX", Guid.NewGuid(), DateTimeOffset.UnixEpoch.AddMinutes(1));
		await Prepare(harness);

		await SyncTests.CoverAsync(harness);

		await harness.UsingAsync(async scope =>
		{
			var context = scope.GetRequiredService<MyloMailDbContext>();
			Assert.Equal(2, await context.Messages.CountAsync());
			var shared = await context
				.Messages.Include(m => m.Occurrences)
				.SingleAsync(m => m.ProviderStableId == $"message-{wearsThree}");
			var labels = await context.Mailboxes.ToDictionaryAsync(m => m.Id, m => m.ProviderMailboxId!);
			Assert.Equal(
				new[] { "IMPORTANT", "INBOX", "RECEIPTS" },
				shared.Occurrences.Select(o => labels[o.MailboxId]).Order(StringComparer.Ordinal)
			);
		});

		// One fetch per message however many labels it wears: two messages, not four occurrences.
		Assert.Equal(2, harness.Provider.AccountWalkMessageIds.Count);
		Assert.Equal(2, harness.Provider.AccountWalkMessageIds.Distinct().Count());
	}

	[Fact]
	public async Task A_walk_fetches_each_message_once_across_every_label_and_page()
	{
		await using var harness = await SyncHarness.CreateAsync(ProviderShapes.Gmail);
		harness.Provider.AddMailbox("INBOX", SpecialUse.Inbox);
		harness.Provider.AddMailbox("IMPORTANT", SpecialUse.Archive);
		harness.Provider.AddMailbox("RECEIPTS");
		for (var index = 0; index < 5; index++)
		{
			var message = Guid.NewGuid();
			var at = DateTimeOffset.UnixEpoch.AddMinutes(index);
			harness.Provider.SeedMessage("INBOX", message, at);
			harness.Provider.SeedMessage("IMPORTANT", message, at);
			harness.Provider.SeedMessage("RECEIPTS", message, at);
		}
		await Prepare(harness);

		await SyncTests.CoverAsync(harness, pageSize: 2);

		Assert.Equal(3, harness.Provider.AccountWalkPages);
		Assert.Equal(5, harness.Provider.AccountWalkMessageIds.Count);
		Assert.Equal(5, harness.Provider.AccountWalkMessageIds.Distinct().Count());
	}

	/// <summary>
	/// A label the account has but this app has not discovered yet cannot become a membership;
	/// the ones it can map still do, and the page does not fail.
	/// </summary>
	[Fact]
	public async Task A_label_with_no_local_mailbox_is_dropped_and_the_rest_of_the_message_is_kept()
	{
		await using var harness = await SyncHarness.CreateAsync(ProviderShapes.Gmail);
		harness.Provider.AddMailbox("INBOX", SpecialUse.Inbox);
		await SyncTests.ReconcileAsync(harness);
		harness.Provider.AddMailbox("FRESH");
		var message = Guid.NewGuid();
		harness.Provider.SeedMessage("INBOX", message, DateTimeOffset.UnixEpoch);
		harness.Provider.SeedMessage("FRESH", message, DateTimeOffset.UnixEpoch);
		await EstablishBaselineAsync(harness);

		await SyncTests.CoverAsync(harness);

		await harness.UsingAsync(async scope =>
		{
			var context = scope.GetRequiredService<MyloMailDbContext>();
			Assert.Single(await context.Messages.ToListAsync());
			var membership = Assert.Single(await context.MessageMailboxes.ToListAsync());
			Assert.Equal(
				"INBOX",
				(await context.Mailboxes.SingleAsync(m => m.Id == membership.MailboxId)).ProviderMailboxId
			);
		});
	}

	/// <summary>
	/// Archived mail wears no label, and mail whose only labels are unknown here has no mailbox
	/// either. Gmail has no "All Mail" label and the app no All Mail mailbox, so a row for it
	/// would be a membership-less tombstone that GC collects (§6) after queueing a raw download.
	/// It is still counted as consumed, so progress against the account estimate stays honest.
	/// </summary>
	[Fact]
	public async Task A_message_that_maps_to_no_mailbox_is_not_materialised_but_is_counted()
	{
		await using var harness = await SyncHarness.CreateAsync(ProviderShapes.Gmail);
		harness.Provider.AddMailbox("INBOX", SpecialUse.Inbox);
		await SyncTests.ReconcileAsync(harness);
		harness.Provider.AddMailbox("FRESH");
		harness.Provider.SeedMessage("INBOX", Guid.NewGuid(), DateTimeOffset.UnixEpoch.AddMinutes(2));
		harness.Provider.SeedMessageWithoutLabels(Guid.NewGuid(), DateTimeOffset.UnixEpoch.AddMinutes(1));
		harness.Provider.SeedMessage("FRESH", Guid.NewGuid(), DateTimeOffset.UnixEpoch);
		await SyncTests.SyncAsync(harness);

		await SyncTests.CoverAsync(harness);

		await harness.UsingAsync(async scope =>
		{
			var context = scope.GetRequiredService<MyloMailDbContext>();
			Assert.Single(await context.Messages.ToListAsync());
			Assert.Single(await context.MessageContentStates.ToListAsync());
			var walk = await context.AccountCoverageStates.SingleAsync();
			Assert.Equal(CoverageStatus.Covered, walk.Status);
			Assert.Equal(3, walk.MessagesFetched);
			Assert.Equal(3, walk.EstimatedTotal);
		});
	}

	/// <summary>
	/// The status bar adds up every mailbox's figures. Reporting the walk's totals on each label
	/// would show several times the account's mail, so they are reported once, on one mailbox.
	/// </summary>
	[Fact]
	public async Task Progress_is_reported_once_so_the_status_bar_does_not_count_a_message_per_label()
	{
		await using var harness = await SyncHarness.CreateAsync(ProviderShapes.Gmail);
		harness.Provider.AddMailbox("INBOX", SpecialUse.Inbox);
		harness.Provider.AddMailbox("IMPORTANT", SpecialUse.Archive);
		harness.Provider.AddMailbox("RECEIPTS");
		for (var index = 0; index < 5; index++)
		{
			var message = Guid.NewGuid();
			var at = DateTimeOffset.UnixEpoch.AddMinutes(index);
			foreach (var label in new[] { "INBOX", "IMPORTANT", "RECEIPTS" })
			{
				harness.Provider.SeedMessage(label, message, at);
			}
		}
		await Prepare(harness);

		Assert.True(await RunPageAsync(harness, pageSize: 2));

		var midway = await SummariesAsync(harness);
		Assert.All(midway, mailbox => Assert.Equal(CoverageStatus.Backfilling, mailbox.Coverage));
		Assert.All(midway, mailbox => Assert.Equal(MailboxAvailability.Usable, mailbox.Availability));
		Assert.Equal(2, midway.Sum(mailbox => mailbox.CoverageMessagesFetched));
		var carrier = midway.Single(mailbox => mailbox.SpecialUse == SpecialUse.Inbox);
		Assert.Equal(2, carrier.CoverageMessagesFetched);
		Assert.Equal(5, carrier.CoverageEstimatedTotal);
		Assert.All(
			midway.Where(mailbox => mailbox.Id != carrier.Id),
			mailbox =>
			{
				Assert.Equal(0, mailbox.CoverageMessagesFetched);
				Assert.Equal(0, mailbox.CoverageEstimatedTotal);
			}
		);

		await SyncTests.CoverAsync(harness, pageSize: 2);

		var done = await SummariesAsync(harness);
		Assert.All(done, mailbox => Assert.Equal(CoverageStatus.Covered, mailbox.Coverage));
		Assert.Equal(5, done.Sum(mailbox => mailbox.CoverageMessagesFetched));
		Assert.Equal(5, done.Sum(mailbox => mailbox.CoverageEstimatedTotal));
	}

	/// <summary>
	/// A bound applies to the walk as a whole: the newest N messages of the account, not N per
	/// label. A quiet label with only old mail is left empty.
	/// </summary>
	[Fact]
	public async Task A_message_bound_means_the_newest_messages_of_the_account_not_of_each_label()
	{
		await using var harness = await SyncHarness.CreateAsync(ProviderShapes.Gmail);
		harness.Provider.AddMailbox("INBOX", SpecialUse.Inbox);
		harness.Provider.AddMailbox("RECEIPTS");
		harness.Provider.SeedMessage("INBOX", Guid.NewGuid(), DateTimeOffset.UnixEpoch);
		for (var index = 1; index <= 3; index++)
		{
			harness.Provider.SeedMessage("RECEIPTS", Guid.NewGuid(), DateTimeOffset.UnixEpoch.AddDays(index));
		}
		await SetBoundAsync(harness, InitialSyncMode.LastNMessages, 2);
		await Prepare(harness);

		await SyncTests.CoverAsync(harness);

		await harness.UsingAsync(async scope =>
		{
			var context = scope.GetRequiredService<MyloMailDbContext>();
			Assert.Equal(2, await context.Messages.CountAsync());
			var inbox = await harness.MailboxAsync(scope, "INBOX");
			var receipts = await harness.MailboxAsync(scope, "RECEIPTS");
			Assert.Equal(0, await context.MessageMailboxes.CountAsync(o => o.MailboxId == inbox.Id));
			Assert.Equal(2, await context.MessageMailboxes.CountAsync(o => o.MailboxId == receipts.Id));
		});
	}

	[Fact]
	public async Task A_month_bound_keeps_only_messages_inside_the_window_whichever_label_they_wear()
	{
		await using var harness = await SyncHarness.CreateAsync(ProviderShapes.Gmail);
		harness.Provider.ServerNow = new DateTimeOffset(2026, 10, 3, 0, 0, 0, TimeSpan.Zero);
		harness.Provider.AddMailbox("INBOX", SpecialUse.Inbox);
		harness.Provider.AddMailbox("RECEIPTS");
		harness.Provider.SeedMessage("INBOX", Guid.NewGuid(), harness.Provider.ServerNow.AddMonths(-5));
		harness.Provider.SeedMessage("RECEIPTS", Guid.NewGuid(), harness.Provider.ServerNow.AddMonths(-1));
		await SetBoundAsync(harness, InitialSyncMode.LastNMonths, 3);
		await Prepare(harness);

		await SyncTests.CoverAsync(harness);

		await harness.UsingAsync(async scope =>
		{
			var context = scope.GetRequiredService<MyloMailDbContext>();
			var stored = await context.Messages.SingleAsync();
			Assert.Equal(harness.Provider.ServerNow.AddMonths(-1), stored.ReceivedAt);
		});
	}

	/// <summary>
	/// A label created after the walk started was never part of it, so it is caught up on its
	/// own once the walk is done — through the ordinary per-mailbox path, whose row carries its
	/// own progress rather than the walk's 0-of-0.
	/// </summary>
	[Fact]
	public async Task A_label_created_after_the_walk_started_is_caught_up_on_its_own_afterwards()
	{
		await using var harness = await SyncHarness.CreateAsync(ProviderShapes.Gmail);
		harness.Provider.AddMailbox("INBOX", SpecialUse.Inbox);
		harness.Provider.SeedMessage("INBOX", Guid.NewGuid(), DateTimeOffset.UnixEpoch.AddMinutes(2));
		harness.Provider.SeedMessage("INBOX", Guid.NewGuid(), DateTimeOffset.UnixEpoch.AddMinutes(1));
		await Prepare(harness);
		Assert.True(await RunPageAsync(harness, pageSize: 1));

		harness.Provider.AddMailbox("FRESH");
		harness.Provider.SeedMessage("FRESH", Guid.NewGuid(), DateTimeOffset.UnixEpoch);
		await SyncTests.ReconcileAsync(harness);

		await harness.UsingAsync(async scope =>
		{
			var context = scope.GetRequiredService<MyloMailDbContext>();
			var fresh = await harness.MailboxAsync(scope, "FRESH");

			// Not part of a walk already running: no row, and no way to page it on its own yet.
			Assert.False(await context.MailboxCoverageStates.AnyAsync(c => c.MailboxId == fresh.Id));
			await Assert.ThrowsAsync<InvalidOperationException>(async () =>
				await scope
					.GetRequiredService<CoverageService>()
					.RunPageAsync(await harness.AccountInScopeAsync(scope), fresh)
			);
		});

		await SyncTests.CoverAsync(harness);

		await harness.UsingAsync(async scope =>
		{
			var context = scope.GetRequiredService<MyloMailDbContext>();
			Assert.Equal(CoverageStatus.Covered, (await context.AccountCoverageStates.SingleAsync()).Status);
			var fresh = await harness.MailboxAsync(scope, "FRESH");
			var row = await context.MailboxCoverageStates.SingleAsync(c => c.MailboxId == fresh.Id);
			Assert.Equal(CoverageStatus.Covered, row.Status);
			Assert.Equal(1, row.MessagesFetched);
			Assert.Equal(1, await context.MessageMailboxes.CountAsync(o => o.MailboxId == fresh.Id));
			Assert.All(
				await context.MailboxCoverageStates.ToListAsync(),
				coverage => Assert.Equal(CoverageStatus.Covered, coverage.Status)
			);
		});
	}

	/// <summary>
	/// The walk fenced by generation: a settings change that lands while a page is in flight
	/// restarts the walk, and the page it was fetching must commit nothing — neither its
	/// messages nor a position that belongs to the old walk.
	/// </summary>
	[Fact]
	public async Task A_page_in_flight_when_the_walk_restarts_commits_nothing()
	{
		await using var harness = await SyncHarness.CreateAsync(ProviderShapes.Gmail);
		harness.Provider.AddMailbox("INBOX", SpecialUse.Inbox);
		for (var index = 0; index < 4; index++)
		{
			harness.Provider.SeedMessage("INBOX", Guid.NewGuid(), DateTimeOffset.UnixEpoch.AddMinutes(index));
		}
		await Prepare(harness);
		harness.Provider.BeforeInitialSyncReturnAsync = async () =>
		{
			harness.Provider.BeforeInitialSyncReturnAsync = null;
			await harness.UsingAsync(scope =>
				scope
					.GetRequiredService<MailHub>()
					.UpdateAccount(
						Settings(harness.Account) with
						{
							InitialSyncMode = InitialSyncMode.LastNMessages,
							InitialSyncBoundValue = 3,
						}
					)
			);
		};

		await Assert.ThrowsAsync<CoverageBaselinePendingException>(() => RunPageAsync(harness, pageSize: 2));

		await harness.UsingAsync(async scope =>
		{
			var context = scope.GetRequiredService<MyloMailDbContext>();
			Assert.Empty(await context.Messages.ToListAsync());
			var walk = await context.AccountCoverageStates.SingleAsync();
			Assert.Equal(CoverageStatus.NotStarted, walk.Status);
			Assert.Equal(1, walk.PolicyGeneration);
			Assert.Null(walk.ResumeToken);
			Assert.Equal(0, walk.MessagesFetched);
		});

		// The restarted walk then runs under the new bound from its own beginning.
		await SyncTests.CoverAsync(harness, pageSize: 2);
		await harness.UsingAsync(async scope =>
		{
			var context = scope.GetRequiredService<MyloMailDbContext>();
			Assert.Equal(3, await context.Messages.CountAsync());
			Assert.Equal(CoverageStatus.Covered, (await context.AccountCoverageStates.SingleAsync()).Status);
		});
	}

	/// <summary>
	/// A walk restarted by a triggered resynchronisation re-lists from the start under the new
	/// baseline, and a page fetched against the old one is discarded the same way.
	/// </summary>
	[Fact]
	public async Task A_page_in_flight_when_a_resync_resets_the_walk_commits_nothing()
	{
		await using var harness = await SyncHarness.CreateAsync(ProviderShapes.Gmail);
		harness.Provider.AddMailbox("INBOX", SpecialUse.Inbox);
		for (var index = 0; index < 4; index++)
		{
			harness.Provider.SeedMessage("INBOX", Guid.NewGuid(), DateTimeOffset.UnixEpoch.AddMinutes(index));
		}
		await Prepare(harness);
		Assert.True(await RunPageAsync(harness, pageSize: 2));
		harness.Provider.BeforeInitialSyncReturnAsync = async () =>
		{
			harness.Provider.BeforeInitialSyncReturnAsync = null;
			harness.Provider.InvalidateCursors();
			await SyncTests.SyncAsync(harness);
		};

		await Assert.ThrowsAsync<CoverageBaselinePendingException>(() => RunPageAsync(harness, pageSize: 2));

		await harness.UsingAsync(async scope =>
		{
			var context = scope.GetRequiredService<MyloMailDbContext>();
			var walk = await context.AccountCoverageStates.SingleAsync();
			Assert.Equal(CoverageStatus.NotStarted, walk.Status);
			Assert.Equal(1, walk.PolicyGeneration);
			Assert.Null(walk.ResumeToken);
			Assert.Equal(0, walk.MessagesFetched);
			Assert.True((await context.ChangeStreamStates.SingleAsync()).IsRebasing);
		});
	}

	/// <summary>
	/// A walk that cannot proceed is recorded once, on the mailbox carrying its progress — not as
	/// one identical problem per label — and resumes from its cursor when the fault clears.
	/// </summary>
	[Fact]
	public async Task A_failed_walk_is_recorded_once_and_resumes_from_its_cursor()
	{
		await using var harness = await SyncHarness.CreateAsync(ProviderShapes.Gmail);
		harness.Provider.AddMailbox("INBOX", SpecialUse.Inbox);
		harness.Provider.AddMailbox("RECEIPTS");
		for (var index = 0; index < 4; index++)
		{
			var message = Guid.NewGuid();
			var at = DateTimeOffset.UnixEpoch.AddMinutes(index);
			harness.Provider.SeedMessage("INBOX", message, at);
			harness.Provider.SeedMessage("RECEIPTS", message, at);
		}
		await Prepare(harness);
		Assert.True(await RunPageAsync(harness, pageSize: 2));
		var firstPage = harness.Provider.AccountWalkMessageIds.ToList();

		harness.Provider.BeforeInitialSyncReturnAsync = () =>
			Task.FromException(new InvalidOperationException("Malformed coverage page."));
		var failing = await harness.UsingAsync(async scope =>
		{
			var coverage = scope.GetRequiredService<CoverageService>();
			var account = await harness.AccountInScopeAsync(scope);
			var fence = await coverage.CaptureFenceAsync(account);
			var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
				coverage.RunAccountPageAsync(account, 2)
			);
			await coverage.RecordAccountFailureAsync(account.Id, fence, failure);
			return failure;
		});

		await harness.UsingAsync(async scope =>
		{
			var context = scope.GetRequiredService<MyloMailDbContext>();
			var walk = await context.AccountCoverageStates.SingleAsync();
			Assert.Equal(CoverageStatus.Failed, walk.Status);
			Assert.Equal(failing.Message, walk.LastError);
			Assert.Equal("2", walk.ResumeToken);

			var rows = await context.MailboxCoverageStates.ToListAsync();
			var failed = Assert.Single(rows, row => row.Status == CoverageStatus.Failed);
			Assert.Equal(failing.Message, failed.LastError);
			Assert.Equal(
				(await harness.MailboxAsync(scope, "INBOX")).Id,
				failed.MailboxId
			);
			Assert.Single(rows, row => row.LastError is not null);
			Assert.Equal(
				CoverageStatus.Backfilling,
				Assert.Single(rows, row => row.Status != CoverageStatus.Failed).Status
			);
		});

		harness.Provider.BeforeInitialSyncReturnAsync = null;
		await SyncTests.CoverAsync(harness, pageSize: 2);

		await harness.UsingAsync(async scope =>
		{
			var context = scope.GetRequiredService<MyloMailDbContext>();
			Assert.Equal(CoverageStatus.Covered, (await context.AccountCoverageStates.SingleAsync()).Status);
			Assert.Equal(4, await context.Messages.CountAsync());
			Assert.All(
				await context.MailboxCoverageStates.ToListAsync(),
				row =>
				{
					Assert.Equal(CoverageStatus.Covered, row.Status);
					Assert.Null(row.LastError);
				}
			);
		});

		// The page committed before the failure was never listed again.
		Assert.All(firstPage, id => Assert.Equal(1, harness.Provider.AccountWalkMessageIds.Count(seen => seen == id)));
	}

	/// <summary>
	/// A failure recorded for a walk that has since been restarted must not mark its replacement
	/// failed: the fence it was issued under no longer matches.
	/// </summary>
	[Fact]
	public async Task A_failure_from_a_restarted_walk_is_not_recorded_against_its_replacement()
	{
		await using var harness = await SyncHarness.CreateAsync(ProviderShapes.Gmail);
		harness.Provider.AddMailbox("INBOX", SpecialUse.Inbox);
		harness.Provider.SeedMessage("INBOX", Guid.NewGuid(), DateTimeOffset.UnixEpoch);
		harness.Provider.SeedMessage("INBOX", Guid.NewGuid(), DateTimeOffset.UnixEpoch.AddMinutes(1));
		await Prepare(harness);
		Assert.True(await RunPageAsync(harness, pageSize: 1));
		var staleFence = await harness.UsingAsync(async scope =>
			await scope
				.GetRequiredService<CoverageService>()
				.CaptureFenceAsync(await harness.AccountInScopeAsync(scope))
		);
		await harness.UsingAsync(scope =>
			scope.GetRequiredService<MailHub>().UpdateAccount(
				Settings(harness.Account) with
				{
					InitialSyncMode = InitialSyncMode.LastNMessages,
					InitialSyncBoundValue = 5,
				}
			)
		);

		await harness.UsingAsync(scope =>
			scope
				.GetRequiredService<CoverageService>()
				.RecordAccountFailureAsync(harness.Account.Id, staleFence, new InvalidOperationException("stale"))
		);

		await harness.UsingAsync(async scope =>
		{
			var context = scope.GetRequiredService<MyloMailDbContext>();
			var walk = await context.AccountCoverageStates.SingleAsync();
			Assert.Equal(CoverageStatus.NotStarted, walk.Status);
			Assert.Null(walk.LastError);
			Assert.All(await context.MailboxCoverageStates.ToListAsync(), row => Assert.Null(row.LastError));
		});
	}

	/// <summary>
	/// Staged history is held back until the walk has covered every mailbox, and replayed only
	/// then, so a stale walk page can never resurrect something history already removed (§3).
	/// </summary>
	[Fact]
	public async Task Staged_history_waits_for_the_whole_walk_and_is_replayed_after_it()
	{
		await using var harness = await SyncHarness.CreateAsync(ProviderShapes.Gmail);
		harness.Provider.AddMailbox("INBOX", SpecialUse.Inbox);
		for (var index = 0; index < 3; index++)
		{
			harness.Provider.SeedMessage("INBOX", Guid.NewGuid(), DateTimeOffset.UnixEpoch.AddMinutes(index));
		}
		await SyncTests.ReconcileAsync(harness);
		Assert.True((await SyncTests.SyncAsync(harness)).Staged);
		Assert.True(await RunPageAsync(harness, pageSize: 2));

		Assert.Equal(0, await ReplayAsync(harness));
		await harness.UsingAsync(async scope =>
			Assert.NotEmpty(await scope.GetRequiredService<MyloMailDbContext>().StagedChangeEvents.ToListAsync())
		);

		await SyncTests.CoverAsync(harness, pageSize: 2);
		Assert.True(await ReplayAsync(harness) > 0);
		await harness.UsingAsync(async scope =>
			Assert.Empty(await scope.GetRequiredService<MyloMailDbContext>().StagedChangeEvents.ToListAsync())
		);
	}

	/// <summary>
	/// A walk that has nothing to cover — no mailboxes yet — neither fails nor claims coverage.
	/// </summary>
	[Fact]
	public async Task A_walk_with_no_mailboxes_does_nothing_and_does_not_claim_to_be_covered()
	{
		await using var harness = await SyncHarness.CreateAsync(ProviderShapes.Gmail);
		await SyncTests.ReconcileAsync(harness);
		await harness.UsingAsync(async scope =>
		{
			var context = scope.GetRequiredService<MyloMailDbContext>();
			context.ChangeStreamStates.Add(
				new ChangeStreamState
				{
					Id = Guid.NewGuid(),
					AccountId = harness.Account.Id,
					CursorKind = CursorKind.GmailHistory,
					CursorState = harness.Provider.CurrentCursor(),
				}
			);
			await context.SaveChangesAsync();
		});

		Assert.False(await RunPageAsync(harness, pageSize: 2));

		await harness.UsingAsync(async scope =>
		{
			var walk = await scope.GetRequiredService<MyloMailDbContext>().AccountCoverageStates.SingleAsync();
			Assert.Equal(CoverageStatus.NotStarted, walk.Status);
		});
	}

	/// <summary>
	/// Topology plus the Gmail baseline the walk waits for. The baseline is recorded directly
	/// rather than by running the change stream: the fake's stream reports one DTO per label
	/// occurrence, so a message wearing several labels would stage the same stable id twice in
	/// a page, which Gmail's history (one entry per message) never does.
	/// </summary>
	private static async Task Prepare(SyncHarness harness)
	{
		await SyncTests.ReconcileAsync(harness);
		await EstablishBaselineAsync(harness);
	}

	private static Task EstablishBaselineAsync(SyncHarness harness) =>
		harness.UsingAsync(async scope =>
		{
			var context = scope.GetRequiredService<MyloMailDbContext>();
			if (!await context.ChangeStreamStates.AnyAsync(state => state.AccountId == harness.Account.Id))
			{
				context.ChangeStreamStates.Add(
					new ChangeStreamState
					{
						Id = Guid.NewGuid(),
						AccountId = harness.Account.Id,
						CursorKind = CursorKind.GmailHistory,
						CursorState = harness.Provider.CurrentCursor(),
						BaselineEstablishedAt = DateTimeOffset.UtcNow,
					}
				);
				await context.SaveChangesAsync();
			}
		});

	private static Task<bool> RunPageAsync(SyncHarness harness, int pageSize) =>
		harness.UsingAsync(async scope =>
			await scope
				.GetRequiredService<CoverageService>()
				.RunAccountPageAsync(await harness.AccountInScopeAsync(scope), pageSize)
		);

	private static Task<int> ReplayAsync(SyncHarness harness) =>
		harness.UsingAsync(async scope =>
			await scope
				.GetRequiredService<ChangeStreamService>()
				.ReplayStagedAsync(await harness.AccountInScopeAsync(scope))
		);

	private static Task<IReadOnlyList<MyloMail.Api.Contracts.MailboxSummaryDto>> SummariesAsync(
		SyncHarness harness
	) =>
		harness.UsingAsync(async scope =>
			(await MailboxSummaryDtoFactory.ListAsync(
				scope.GetRequiredService<MyloMailDbContext>(),
				harness.Account.Id
			)).Where(mailbox => !mailbox.IsSynthesized).ToList() as IReadOnlyList<MyloMail.Api.Contracts.MailboxSummaryDto>
		);

	private static Task SetBoundAsync(SyncHarness harness, InitialSyncMode mode, int bound) =>
		harness.UsingAsync(async scope =>
		{
			var account = await harness.AccountInScopeAsync(scope);
			account.InitialSyncMode = mode;
			account.InitialSyncBoundValue = bound;
			await scope.GetRequiredService<MyloMailDbContext>().SaveChangesAsync();
		});

	private static MyloMail.Api.Contracts.AccountSettingsDto Settings(Account account) =>
		new(
			account.Id,
			account.DisplayName,
			account.Color,
			account.PollIntervalSeconds,
			account.PollingEnabled,
			account.UndoSendDelaySeconds,
			account.NotificationsEnabled,
			account.InitialSyncMode,
			account.InitialSyncBoundValue,
			account.CertificateTrustMode,
			account.AttachmentSizeLimitOverride,
			AppendToSentOnSend: null,
			MaxMessageDownloadMegabytes: 128,
			GroupConversations: false
		);
}
