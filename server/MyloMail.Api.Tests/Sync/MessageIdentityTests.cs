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

/// <summary>
/// Matching an observation to a local message (§1). The failure these guard against is not a
/// missing row but a wrong one: two distinct messages merged into a single local identity,
/// which no later sync undoes because the local row looks entirely plausible afterwards.
/// </summary>
public class MessageIdentityTests
{
	/// <summary>
	/// An IMAP UID is unique within a folder and nowhere else, so an occurrence lookup that
	/// ignores the mailbox merges the message holding UID 2 in one folder with the unrelated
	/// message holding UID 2 in another.
	/// </summary>
	/// <remarks>
	/// Found in the end-to-end suite, not here: a draft left in the server's Drafts folder by
	/// an earlier spec silently rewrote a seeded inbox message's subject, and the only visible
	/// symptom was a search returning a message that did not match the query.
	/// </remarks>
	[Fact]
	public async Task Occurrence_ids_that_collide_across_mailboxes_stay_separate_messages()
	{
		await using var harness = await SyncHarness.CreateAsync(ProviderShapes.Imap(ImapCapabilityTier.QResync));
		harness.Provider.AddMailbox("INBOX", SpecialUse.Inbox);
		harness.Provider.AddMailbox("Archive", SpecialUse.Archive);
		await SyncTests.ReconcileAsync(harness);

		await harness.UsingAsync(async scope =>
		{
			var context = scope.GetRequiredService<MyloMailDbContext>();
			var account = await harness.AccountInScopeAsync(scope);
			var inbox = await harness.MailboxAsync(scope, "INBOX");
			var archive = await harness.MailboxAsync(scope, "Archive");
			var mailboxes = new Dictionary<string, Mailbox> { ["INBOX"] = inbox, ["Archive"] = archive };

			var ingestor = scope.GetRequiredService<MessageIngestor>();

			// Two ingests with a save between them, because matching queries the database: a
			// single page's occurrences are not visible to each other, so ingesting both at
			// once would pass whether or not the lookup is scoped to a mailbox.
			await ingestor.IngestAsync(
				account,
				[Observation("INBOX", "2", "In the inbox")],
				mailboxes,
				GenerationSnapshot.Capture(mailboxes.Values)
			);
			await context.SaveChangesAsync();

			await ingestor.IngestAsync(
				account,
				[Observation("Archive", "2", "In the archive")],
				mailboxes,
				GenerationSnapshot.Capture(mailboxes.Values)
			);
			await context.SaveChangesAsync();

			var subjects = await context.Messages.Select(m => m.Subject).OrderBy(s => s).ToListAsync();
			Assert.Equal(["In the archive", "In the inbox"], subjects);
		});
	}

	/// <summary>
	/// A message in the Drafts folder is a Draft, never a Message (§1). Excluding it only when
	/// its occurrences are written leaves a bare message row behind — matched, applied and
	/// created, but belonging to no mailbox.
	/// </summary>
	[Fact]
	public async Task A_drafts_folder_observation_never_becomes_or_overwrites_a_message()
	{
		await using var harness = await SyncHarness.CreateAsync(ProviderShapes.Imap(ImapCapabilityTier.QResync));
		harness.Provider.AddMailbox("INBOX", SpecialUse.Inbox);
		harness.Provider.AddMailbox("Drafts", SpecialUse.Drafts);
		await SyncTests.ReconcileAsync(harness);

		await harness.UsingAsync(async scope =>
		{
			var context = scope.GetRequiredService<MyloMailDbContext>();
			var account = await harness.AccountInScopeAsync(scope);
			var inbox = await harness.MailboxAsync(scope, "INBOX");
			var drafts = await harness.MailboxAsync(scope, "Drafts");
			var mailboxes = new Dictionary<string, Mailbox> { ["INBOX"] = inbox, ["Drafts"] = drafts };
			var ingestor = scope.GetRequiredService<MessageIngestor>();

			await ingestor.IngestAsync(
				account,
				[Observation("INBOX", "2", "Real mail")],
				mailboxes,
				GenerationSnapshot.Capture(mailboxes.Values)
			);
			await context.SaveChangesAsync();

			await ingestor.IngestAsync(
				account,
				[Observation("Drafts", "2", "Half-written")],
				mailboxes,
				GenerationSnapshot.Capture(mailboxes.Values)
			);
			await context.SaveChangesAsync();

			var message = await context.Messages.SingleAsync();
			Assert.Equal("Real mail", message.Subject);
		});
	}

	/// <summary>
	/// Under Gmail's label model a draft carries DRAFT <i>alongside</i> its other labels, so
	/// one drafts occurrence has to exclude the whole observation. Dropping only the drafts
	/// occurrence would materialise the rest as ordinary mail, and the draft would then exist
	/// locally twice — once as something the user can edit and once as a message.
	/// </summary>
	[Fact]
	public async Task A_gmail_draft_is_excluded_by_its_draft_label_whatever_else_it_carries()
	{
		await using var harness = await SyncHarness.CreateAsync(ProviderShapes.Gmail);
		harness.Provider.AddMailbox("INBOX", SpecialUse.Inbox);
		harness.Provider.AddMailbox("DRAFT", SpecialUse.Drafts);
		await SyncTests.ReconcileAsync(harness);

		await harness.UsingAsync(async scope =>
		{
			var context = scope.GetRequiredService<MyloMailDbContext>();
			var account = await harness.AccountInScopeAsync(scope);
			var inbox = await harness.MailboxAsync(scope, "INBOX");
			var drafts = await harness.MailboxAsync(scope, "DRAFT");
			var mailboxes = new Dictionary<string, Mailbox> { ["INBOX"] = inbox, ["DRAFT"] = drafts };

			await scope
				.GetRequiredService<MessageIngestor>()
				.IngestAsync(
					account,
					[
						new MessageDto
						{
							Occurrences =
							[
								new MessageOccurrenceDto("DRAFT", "m1"),
								new MessageOccurrenceDto("INBOX", "m1"),
							],
							ProviderStableId = "m1",
							Subject = "Half-written",
							ReceivedAt = DateTimeOffset.UnixEpoch,
						},
					],
					mailboxes,
					GenerationSnapshot.Capture(mailboxes.Values)
				);
			await context.SaveChangesAsync();

			Assert.Empty(await context.Messages.ToListAsync());
		});
	}

	/// <summary>
	/// Matching now preloads every candidate row for the whole page in three queries (§1, an
	/// N+1 fix), by deliberately over-fetching: one query for every occurrence id across every
	/// mailbox this page touches, then keying the results by the exact
	/// <c>(MailboxId, OccurrenceId)</c> pair in memory. If that keying were ever done by
	/// occurrence id alone, two existing messages that happen to share an occurrence id in
	/// different mailboxes — an ordinary IMAP UID collision — would merge into one the moment
	/// both were observed in the very same sync page, not just across separate pages.
	/// </summary>
	[Fact]
	public async Task Occurrence_ids_that_collide_across_mailboxes_stay_separate_within_one_page()
	{
		await using var harness = await SyncHarness.CreateAsync(ProviderShapes.Imap(ImapCapabilityTier.QResync));
		harness.Provider.AddMailbox("INBOX", SpecialUse.Inbox);
		harness.Provider.AddMailbox("Archive", SpecialUse.Archive);
		await SyncTests.ReconcileAsync(harness);

		await harness.UsingAsync(async scope =>
		{
			var context = scope.GetRequiredService<MyloMailDbContext>();
			var account = await harness.AccountInScopeAsync(scope);
			var inbox = await harness.MailboxAsync(scope, "INBOX");
			var archive = await harness.MailboxAsync(scope, "Archive");
			var mailboxes = new Dictionary<string, Mailbox> { ["INBOX"] = inbox, ["Archive"] = archive };
			var ingestor = scope.GetRequiredService<MessageIngestor>();

			// Establish both messages first, each already occupying occurrence id "2" in its
			// own mailbox.
			await ingestor.IngestAsync(
				account,
				[Observation("INBOX", "2", "In the inbox"), Observation("Archive", "2", "In the archive")],
				mailboxes,
				GenerationSnapshot.Capture(mailboxes.Values)
			);
			await context.SaveChangesAsync();

			// Then re-observe both in the SAME page, so the batched preload's over-fetched
			// occurrence rows for occurrence id "2" span both mailboxes at once.
			await ingestor.IngestAsync(
				account,
				[
					Observation("INBOX", "2", "Inbox, updated"),
					Observation("Archive", "2", "Archive, updated"),
				],
				mailboxes,
				GenerationSnapshot.Capture(mailboxes.Values)
			);
			await context.SaveChangesAsync();

			var subjects = await context.Messages.Select(m => m.Subject).OrderBy(s => s).ToListAsync();
			Assert.Equal(["Archive, updated", "Inbox, updated"], subjects);
		});
	}

	/// <summary>
	/// <see cref="MessageIngestor.ApplyFlagChangesAsync"/> batches a whole mailbox's flag
	/// changes into one preload query (§3, an N+1 fix over one query per occurrence). The
	/// preload is keyed by occurrence id within a single mailbox, so if it were ever done
	/// without also filtering by <see cref="Mailbox.Id"/>, an occurrence id that collides
	/// across two mailboxes would let a flag change meant for one silently update the other's
	/// message instead.
	/// </summary>
	[Fact]
	public async Task Flag_changes_batched_in_one_call_do_not_cross_mailboxes_on_a_colliding_occurrence_id()
	{
		await using var harness = await SyncHarness.CreateAsync(ProviderShapes.Imap(ImapCapabilityTier.QResync));
		harness.Provider.AddMailbox("INBOX", SpecialUse.Inbox);
		harness.Provider.AddMailbox("Archive", SpecialUse.Archive);
		await SyncTests.ReconcileAsync(harness);

		await harness.UsingAsync(async scope =>
		{
			var context = scope.GetRequiredService<MyloMailDbContext>();
			var account = await harness.AccountInScopeAsync(scope);
			var inbox = await harness.MailboxAsync(scope, "INBOX");
			var archive = await harness.MailboxAsync(scope, "Archive");
			var mailboxes = new Dictionary<string, Mailbox> { ["INBOX"] = inbox, ["Archive"] = archive };
			var ingestor = scope.GetRequiredService<MessageIngestor>();

			// Both messages start unread, each occupying occurrence id "2" in its own mailbox.
			await ingestor.IngestAsync(
				account,
				[Observation("INBOX", "2", "In the inbox"), Observation("Archive", "2", "In the archive")],
				mailboxes,
				GenerationSnapshot.Capture(mailboxes.Values)
			);
			await context.SaveChangesAsync();

			// Only the inbox's occurrence "2" is reported read — a single batched call against
			// the inbox mailbox, exactly what ChangeStreamService issues per mailbox group.
			var changed = await ingestor.ApplyFlagChangesAsync(
				inbox,
				[new OccurrenceFlagChange("INBOX", "2", IsRead: true, IsFlagged: null)],
				GenerationSnapshot.Capture(mailboxes.Values)
			);
			await context.SaveChangesAsync();

			Assert.Equal("In the inbox", Assert.Single(changed).Subject);
			var messages = await context.Messages.ToDictionaryAsync(m => m.Subject);
			Assert.True(messages["In the inbox"].IsRead);
			Assert.False(messages["In the archive"].IsRead);
		});
	}

	/// <summary>
	/// The removal-side analog of the flag-change test above: <see cref="MessageIngestor.RemoveOccurrencesAsync"/>
	/// batches a whole mailbox's expunges into one preload query, keyed by occurrence id within
	/// a single mailbox. If that preload were ever done without also filtering by
	/// <see cref="Mailbox.Id"/>, an occurrence id that collides across two mailboxes would let a
	/// removal meant for one delete the other's membership instead.
	/// </summary>
	[Fact]
	public async Task Removals_batched_in_one_call_do_not_cross_mailboxes_on_a_colliding_occurrence_id()
	{
		await using var harness = await SyncHarness.CreateAsync(ProviderShapes.Imap(ImapCapabilityTier.QResync));
		harness.Provider.AddMailbox("INBOX", SpecialUse.Inbox);
		harness.Provider.AddMailbox("Archive", SpecialUse.Archive);
		await SyncTests.ReconcileAsync(harness);

		await harness.UsingAsync(async scope =>
		{
			var context = scope.GetRequiredService<MyloMailDbContext>();
			var account = await harness.AccountInScopeAsync(scope);
			var inbox = await harness.MailboxAsync(scope, "INBOX");
			var archive = await harness.MailboxAsync(scope, "Archive");
			var mailboxes = new Dictionary<string, Mailbox> { ["INBOX"] = inbox, ["Archive"] = archive };
			var ingestor = scope.GetRequiredService<MessageIngestor>();

			// Both messages occupy occurrence id "2", each in its own mailbox.
			await ingestor.IngestAsync(
				account,
				[Observation("INBOX", "2", "In the inbox"), Observation("Archive", "2", "In the archive")],
				mailboxes,
				GenerationSnapshot.Capture(mailboxes.Values)
			);
			await context.SaveChangesAsync();

			// Only the inbox's occurrence "2" is reported removed — a single batched call
			// against the inbox mailbox, exactly what ChangeStreamService issues per mailbox
			// group.
			await ingestor.RemoveOccurrencesAsync(inbox, ["2"], GenerationSnapshot.Capture(mailboxes.Values));
			await context.SaveChangesAsync();

			var remaining = await context.MessageMailboxes.SingleAsync();
			var message = await context.Messages.SingleAsync(m => m.Id == remaining.MessageId);
			Assert.Equal("In the archive", message.Subject);
		});
	}

	private static MessageDto Observation(string mailbox, string occurrenceId, string subject) =>
		new()
		{
			Occurrences = [new MessageOccurrenceDto(mailbox, occurrenceId)],
			Subject = subject,
			ReceivedAt = DateTimeOffset.UnixEpoch,
		};
}
