using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MyloMail.Api.Domain;
using MyloMail.Api.Persistence;
using MyloMail.Api.Providers.Contracts;
using Xunit;

namespace MyloMail.Api.Tests.Persistence;

/// <summary>
/// The schema decisions from §1 that look like mistakes. Each was arrived at by finding the
/// simpler version broken; a failure here means an invariant was undone, not that the test
/// is wrong.
/// </summary>
public sealed class SchemaInvariantTests
{
	/// <summary>
	/// IMAP UIDs are folder-scoped and change when a message moves, so a single provider id
	/// on the message cannot address it on the server once folders are decoupled.
	/// </summary>
	[Fact]
	public async Task Provider_occurrence_identity_lives_on_the_occurrence_not_the_message()
	{
		await using var database = new TestDatabase();
		await using var scope = database.CreateScope();
		var model = scope.ServiceProvider.GetRequiredService<MyloMailDbContext>().Model;

		var message = model.FindEntityType(typeof(Message))!;
		Assert.Null(message.FindProperty(nameof(MessageMailbox.ProviderOccurrenceId)));

		var occurrence = model.FindEntityType(typeof(MessageMailbox))!;
		Assert.NotNull(occurrence.FindProperty(nameof(MessageMailbox.ProviderOccurrenceId)));
	}

	/// <summary>
	/// A message belongs to zero or more mailboxes. Gmail's canonical label model means
	/// several at once; under Graph's folder-scoped delta a move can leave it with none.
	/// </summary>
	[Fact]
	public async Task A_message_may_belong_to_several_mailboxes_at_once()
	{
		await using var database = new TestDatabase();
		await database.MigrateAsync();

		var (accountId, inboxId, receiptsId, messageId) = (
			Guid.NewGuid(),
			Guid.NewGuid(),
			Guid.NewGuid(),
			Guid.NewGuid()
		);

		await using var scope = database.CreateScope();
		var context = scope.ServiceProvider.GetRequiredService<MyloMailDbContext>();

		context.Accounts.Add(new Account { Id = accountId, DisplayName = "Gmail", ProviderType = ProviderType.Gmail });
		context.Mailboxes.Add(NewMailbox(inboxId, accountId, "INBOX"));
		context.Mailboxes.Add(NewMailbox(receiptsId, accountId, "Receipts"));
		context.Messages.Add(
			new Message
			{
				Id = messageId,
				AccountId = accountId,
				ProviderStableId = "gmail-1",
				ReceivedAt = DateTimeOffset.UnixEpoch,
				From = [new Address("Alice", "alice@example.org")],
				Occurrences =
				[
					new MessageMailbox { Id = Guid.NewGuid(), MailboxId = inboxId, ProviderOccurrenceId = "gmail-1" },
					new MessageMailbox { Id = Guid.NewGuid(), MailboxId = receiptsId, ProviderOccurrenceId = "gmail-1" },
				],
			}
		);
		await context.SaveChangesAsync();

		context.ChangeTracker.Clear();
		var stored = await context.Messages.Include(m => m.Occurrences).SingleAsync(m => m.Id == messageId);
		Assert.Equal(2, stored.Occurrences.Count);
		Assert.Equal("alice@example.org", Assert.Single(stored.From).Email);
	}

	/// <summary>
	/// Counts are two different numbers. The locally computed count reflects only what has
	/// been downloaded, which under a bounded sync is simply wrong as a mailbox total.
	/// </summary>
	[Fact]
	public async Task Mailbox_carries_provider_reported_counts_separately()
	{
		await using var database = new TestDatabase();
		await using var scope = database.CreateScope();
		var mailbox = scope
			.ServiceProvider.GetRequiredService<MyloMailDbContext>()
			.Model.FindEntityType(typeof(Mailbox))!;

		Assert.NotNull(mailbox.FindProperty(nameof(Mailbox.ProviderTotalCount)));
		Assert.NotNull(mailbox.FindProperty(nameof(Mailbox.ProviderUnreadCount)));
	}

	/// <summary>
	/// Cursor state is structured and versioned per provider. A single opaque string would
	/// pretend the three are equivalent, and a later migration would have to guess.
	/// </summary>
	[Fact]
	public async Task Cursor_state_round_trips_with_its_provider_shape_and_version()
	{
		await using var database = new TestDatabase();
		await database.MigrateAsync();

		var accountId = Guid.NewGuid();
		var mailboxId = Guid.NewGuid();
		var streamId = Guid.NewGuid();

		await using (var scope = database.CreateScope())
		{
			var context = scope.ServiceProvider.GetRequiredService<MyloMailDbContext>();
			context.Accounts.Add(new Account { Id = accountId, DisplayName = "IMAP", ProviderType = ProviderType.Imap });
			context.Mailboxes.Add(NewMailbox(mailboxId, accountId, "INBOX"));
			context.ChangeStreamStates.Add(
				new ChangeStreamState
				{
					Id = streamId,
					AccountId = accountId,
					MailboxId = mailboxId,
					CursorKind = CursorKind.ImapUid,
					CursorState = new ImapUidCursor(12345, 900, 42, null),
				}
			);
			await context.SaveChangesAsync();
		}

		await using (var scope = database.CreateScope())
		{
			var context = scope.ServiceProvider.GetRequiredService<MyloMailDbContext>();
			var stream = await context.ChangeStreamStates.SingleAsync(s => s.Id == streamId);
			var cursor = Assert.IsType<ImapUidCursor>(stream.CursorState);

			Assert.Equal(12345u, cursor.UidValidity);
			Assert.Equal(900u, cursor.HighestKnownUid);
			Assert.Equal(42ul, cursor.HighestModSeq);
			Assert.Equal(1, cursor.Version);
		}
	}

	/// <summary>
	/// Gmail's change stream is account-scoped: one row with a null mailbox. Per-label
	/// cursors would be fiction and would race between label jobs.
	/// </summary>
	[Fact]
	public async Task An_account_has_at_most_one_account_scoped_change_stream()
	{
		await using var database = new TestDatabase();
		await database.MigrateAsync();

		var accountId = Guid.NewGuid();
		await using var scope = database.CreateScope();
		var context = scope.ServiceProvider.GetRequiredService<MyloMailDbContext>();

		context.Accounts.Add(new Account { Id = accountId, DisplayName = "Gmail", ProviderType = ProviderType.Gmail });
		context.ChangeStreamStates.Add(
			new ChangeStreamState { Id = Guid.NewGuid(), AccountId = accountId, CursorKind = CursorKind.GmailHistory }
		);
		await context.SaveChangesAsync();

		context.ChangeStreamStates.Add(
			new ChangeStreamState { Id = Guid.NewGuid(), AccountId = accountId, CursorKind = CursorKind.GmailHistory }
		);

		await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
	}

	/// <summary>
	/// The default identity's address is the authoritative address for the account —
	/// <see cref="Account"/> deliberately has none of its own — so two defaults is not a
	/// display glitch but two answers to which address this account sends from.
	/// </summary>
	[Fact]
	public async Task An_account_has_at_most_one_default_send_identity()
	{
		await using var database = new TestDatabase();
		await database.MigrateAsync();

		var accountId = Guid.NewGuid();
		await using var scope = database.CreateScope();
		var context = scope.ServiceProvider.GetRequiredService<MyloMailDbContext>();

		context.Accounts.Add(new Account { Id = accountId, DisplayName = "IMAP", ProviderType = ProviderType.Imap });
		context.SendIdentities.Add(NewIdentity(accountId, "primary@example.org", isDefault: true));

		// A second, non-default identity is ordinary: aliases are the reason the table exists.
		context.SendIdentities.Add(NewIdentity(accountId, "alias@example.org", isDefault: false));
		await context.SaveChangesAsync();

		context.SendIdentities.Add(NewIdentity(accountId, "second@example.org", isDefault: true));

		await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
	}

	private static SendIdentity NewIdentity(Guid accountId, string address, bool isDefault) =>
		new()
		{
			Id = Guid.NewGuid(),
			AccountId = accountId,
			DisplayName = address,
			EmailAddress = address,
			IsDefault = isDefault,
		};

	private static Mailbox NewMailbox(Guid id, Guid accountId, string name) =>
		new()
		{
			Id = id,
			AccountId = accountId,
			ProviderMailboxId = name,
			Name = name,
			SpecialUse = SpecialUse.None,
		};
}
