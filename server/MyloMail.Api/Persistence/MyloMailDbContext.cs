using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MyloMail.Api.Domain;

namespace MyloMail.Api.Persistence;

/// <summary>
/// The one authoritative database, <c>app.db</c> (§9). There is deliberately no second
/// database: Hangfire storage is in-memory and non-authoritative, and startup
/// reconciliation rebuilds outstanding work from these tables.
/// </summary>
public class MyloMailDbContext(DbContextOptions<MyloMailDbContext> options) : DbContext(options)
{
	public DbSet<Account> Accounts => Set<Account>();
	public DbSet<SendIdentity> SendIdentities => Set<SendIdentity>();
	public DbSet<AccountTrustedCertificate> AccountTrustedCertificates => Set<AccountTrustedCertificate>();
	public DbSet<Mailbox> Mailboxes => Set<Mailbox>();
	public DbSet<Domain.ImapMailboxMetadata> ImapMailboxMetadata => Set<Domain.ImapMailboxMetadata>();

	public DbSet<Message> Messages => Set<Message>();
	public DbSet<MessageMailbox> MessageMailboxes => Set<MessageMailbox>();
	public DbSet<Domain.MessageHeaders> MessageHeaders => Set<Domain.MessageHeaders>();
	public DbSet<MessageBody> MessageBodies => Set<MessageBody>();
	public DbSet<MessageRaw> MessageRaws => Set<MessageRaw>();
	public DbSet<MessageContentState> MessageContentStates => Set<MessageContentState>();
	public DbSet<Attachment> Attachments => Set<Attachment>();
	public DbSet<MessageSearchContent> MessageSearchContents => Set<MessageSearchContent>();

	public DbSet<MailboxTopologySyncState> MailboxTopologySyncStates => Set<MailboxTopologySyncState>();
	public DbSet<MailboxCoverageState> MailboxCoverageStates => Set<MailboxCoverageState>();
	public DbSet<ChangeStreamState> ChangeStreamStates => Set<ChangeStreamState>();
	public DbSet<IntegrityReconciliationState> IntegrityReconciliationStates => Set<IntegrityReconciliationState>();
	public DbSet<StagedChangeEvent> StagedChangeEvents => Set<StagedChangeEvent>();

	public DbSet<MutationItem> MutationItems => Set<MutationItem>();
	public DbSet<MutationExecutionAttempt> MutationExecutionAttempts => Set<MutationExecutionAttempt>();
	public DbSet<MutationExecutionAttemptItem> MutationExecutionAttemptItems =>
		Set<MutationExecutionAttemptItem>();
	public DbSet<MessagePendingChange> MessagePendingChanges => Set<MessagePendingChange>();
	public DbSet<OutboxItem> OutboxItems => Set<OutboxItem>();

	public DbSet<Draft> Drafts => Set<Draft>();
	public DbSet<Calendar> Calendars => Set<Calendar>();
	public DbSet<CalendarEvent> CalendarEvents => Set<CalendarEvent>();
	public DbSet<Domain.AppSettings> AppSettings => Set<Domain.AppSettings>();
	public DbSet<CredentialFallbackSettings> CredentialFallbackSettings => Set<CredentialFallbackSettings>();
	public DbSet<EncryptedCredential> EncryptedCredentials => Set<EncryptedCredential>();
	public DbSet<NotificationRecord> NotificationRecords => Set<NotificationRecord>();
	public DbSet<ExportJob> ExportJobs => Set<ExportJob>();

	protected override void OnModelCreating(ModelBuilder model)
	{
		ConfigureAccounts(model);
		ConfigureMailboxes(model);
		ConfigureMessages(model);
		ConfigureContent(model);
		ConfigureSyncState(model);
		ConfigureMutations(model);
		ConfigureComposition(model);
		ConfigureCalendar(model);
		ConfigureNotifications(model);
		ConfigureExport(model);
		ConfigureCredentialFallback(model);

		model.Entity<Domain.AppSettings>(e =>
		{
			e.HasKey(x => x.Id);
			e.Property(x => x.Id).ValueGeneratedNever();
			e.ToTable(t => t.HasCheckConstraint("CK_AppSettings_SingleRow", "\"Id\" = 1"));
		});
	}

	private static void ConfigureCredentialFallback(ModelBuilder model)
	{
		model.Entity<CredentialFallbackSettings>(e =>
		{
			e.HasKey(x => x.Id);
			e.Property(x => x.Id).ValueGeneratedNever();
			e.ToTable(t => t.HasCheckConstraint("CK_CredentialFallbackSettings_SingleRow", "\"Id\" = 1"));
		});

		model.Entity<EncryptedCredential>(e =>
		{
			e.HasKey(x => x.AccountId);
		});
	}

	private static void ConfigureAccounts(ModelBuilder model)
	{
		model.Entity<Account>(e =>
		{
			e.HasKey(x => x.Id);
			e.Property(x => x.ProviderConfig).HasJsonConversion();
			e.Property(x => x.DisplayName).IsRequired();
		});

		model.Entity<SendIdentity>(e =>
		{
			e.HasKey(x => x.Id);
			e.HasOne<Account>()
				.WithMany()
				.HasForeignKey(x => x.AccountId)
				.OnDelete(DeleteBehavior.Cascade);
			e.HasIndex(x => x.AccountId);

			// Exactly one default per account. The default identity's address is the
			// authoritative address for the account, so two of them is not a display
			// glitch — it is two answers to "who is this account".
			e.HasIndex(x => x.AccountId)
				.HasDatabaseName("IX_SendIdentities_AccountId_Default")
				.IsUnique()
				.HasFilter("\"IsDefault\" = 1");
		});

		model.Entity<AccountTrustedCertificate>(e =>
		{
			e.HasKey(x => x.Id);
			e.HasOne<Account>()
				.WithMany()
				.HasForeignKey(x => x.AccountId)
				.OnDelete(DeleteBehavior.Cascade);
			e.HasIndex(x => new { x.AccountId, x.Thumbprint }).IsUnique();
		});
	}

	private static void ConfigureMailboxes(ModelBuilder model)
	{
		model.Entity<Mailbox>(e =>
		{
			e.HasKey(x => x.Id);
			e.HasOne<Account>()
				.WithMany()
				.HasForeignKey(x => x.AccountId)
				.OnDelete(DeleteBehavior.Cascade);

			// Self-referencing hierarchy. Restrict rather than cascade: a topology
			// reconciliation that deletes a parent must decide what happens to children
			// explicitly, not have the database silently remove a subtree.
			e.HasOne<Mailbox>()
				.WithMany()
				.HasForeignKey(x => x.ParentId)
				.OnDelete(DeleteBehavior.Restrict);

			// Provider mailbox ids are unique within an account where they exist at all.
			// Synthesised Gmail hierarchy nodes have none, and SQLite treats NULLs as
			// distinct, so several may coexist.
			e.HasIndex(x => new { x.AccountId, x.ProviderMailboxId }).IsUnique();

			e.HasOne(x => x.ImapMetadata)
				.WithOne()
				.HasForeignKey<ImapMailboxMetadata>(x => x.MailboxId)
				.OnDelete(DeleteBehavior.Cascade);
		});

		model.Entity<ImapMailboxMetadata>(e => e.HasKey(x => x.MailboxId));
	}

	private static void ConfigureMessages(ModelBuilder model)
	{
		model.Entity<Message>(e =>
		{
			e.HasKey(x => x.Id);
			e.HasOne<Account>()
				.WithMany()
				.HasForeignKey(x => x.AccountId)
				.OnDelete(DeleteBehavior.Cascade);

			e.Property(x => x.From).HasAddressListConversion();
			e.Property(x => x.To).HasAddressListConversion();
			e.Property(x => x.Cc).HasAddressListConversion();
			e.Property(x => x.Bcc).HasAddressListConversion();
			e.Property(x => x.ReplyToAddresses).HasAddressListConversion();
			e.Property(x => x.SenderAddress).HasJsonConversion();

			// Matching an incoming provider object prefers ProviderStableId; it is unique
			// per account where the provider supplies one, and absent for IMAP.
			e.HasIndex(x => new { x.AccountId, x.ProviderStableId }).IsUnique();

			// The (AccountId, MessageIdHeader, ReceivedAt) matching heuristic, and the
			// list view's ordering.
			e.HasIndex(x => new { x.AccountId, x.MessageIdHeader });
			e.HasIndex(x => new { x.AccountId, x.ReceivedAt });
		});

		model.Entity<MessageMailbox>(e =>
		{
			e.HasKey(x => x.Id);
			e.HasOne<Message>()
				.WithMany(x => x.Occurrences)
				.HasForeignKey(x => x.MessageId)
				.OnDelete(DeleteBehavior.Cascade);
			e.HasOne<Mailbox>()
				.WithMany()
				.HasForeignKey(x => x.MailboxId)
				.OnDelete(DeleteBehavior.Cascade);

			// One row per message per mailbox, and one occurrence id per mailbox.
			e.HasIndex(x => new { x.MessageId, x.MailboxId }).IsUnique();
			e.HasIndex(x => new { x.MailboxId, x.ProviderOccurrenceId }).IsUnique();
		});
	}

	private static void ConfigureContent(ModelBuilder model)
	{
		ConfigureMessageOwned<MessageHeaders>(model, e =>
		{
			e.Property(x => x.Headers).HasJsonConversion();
		});
		ConfigureMessageOwned<MessageBody>(model);
		ConfigureMessageOwned<MessageRaw>(model);
		ConfigureMessageOwned<MessageContentState>(model, e =>
		{
			// Background content acquisition claims work by status.
			e.HasIndex(x => x.Status);
		});

		model.Entity<Attachment>(e =>
		{
			e.HasKey(x => x.Id);
			e.HasOne<Message>()
				.WithMany()
				.HasForeignKey(x => x.MessageId)
				.OnDelete(DeleteBehavior.Cascade);
			e.HasIndex(x => x.MessageId);
		});

		model.Entity<MessageSearchContent>(e =>
		{
			// An INTEGER key, not the Guid used elsewhere: FTS5 external-content tables
			// require a stable integer rowid (§8).
			e.HasKey(x => x.RowId);
			e.Property(x => x.RowId).ValueGeneratedOnAdd();
			// Restrict, not Cascade. The FTS5 index is external-content: deleting this row
			// without first removing the terms it mirrors leaves the index describing a row
			// that no longer exists, and SQLite reports that as corruption on some later,
			// unrelated write. Restricting makes the ordering a database rule rather than
			// something tombstone collection has to remember (§6, §8).
			e.HasOne<Message>()
				.WithMany()
				.HasForeignKey(x => x.MessageId)
				.OnDelete(DeleteBehavior.Restrict);
			e.HasIndex(x => x.MessageId).IsUnique();
		});
	}

	private static void ConfigureMessageOwned<T>(ModelBuilder model, Action<EntityTypeBuilder<T>>? extra = null)
		where T : class
	{
		model.Entity<T>(e =>
		{
			e.HasKey("MessageId");
			e.HasOne<Message>()
				.WithOne()
				.HasForeignKey<T>("MessageId")
				.OnDelete(DeleteBehavior.Cascade);
			extra?.Invoke(e);
		});
	}

	private static void ConfigureSyncState(ModelBuilder model)
	{
		model.Entity<MailboxTopologySyncState>(e =>
		{
			e.HasKey(x => x.AccountId);
			e.HasOne<Account>()
				.WithOne()
				.HasForeignKey<MailboxTopologySyncState>(x => x.AccountId)
				.OnDelete(DeleteBehavior.Cascade);
		});

		model.Entity<MailboxCoverageState>(e =>
		{
			e.HasKey(x => x.MailboxId);
			e.HasOne<Mailbox>()
				.WithOne()
				.HasForeignKey<MailboxCoverageState>(x => x.MailboxId)
				.OnDelete(DeleteBehavior.Cascade);
		});

		model.Entity<IntegrityReconciliationState>(e =>
		{
			e.HasKey(x => x.MailboxId);
			e.HasOne<Mailbox>()
				.WithOne()
				.HasForeignKey<IntegrityReconciliationState>(x => x.MailboxId)
				.OnDelete(DeleteBehavior.Cascade);
		});

		model.Entity<StagedChangeEvent>(e =>
		{
			e.HasKey(x => x.Id);
			e.HasOne<Account>()
				.WithMany()
				.HasForeignKey(x => x.AccountId)
				.OnDelete(DeleteBehavior.Cascade);

			// Replay order is the observation order, and it must be unambiguous.
			e.HasIndex(x => new { x.AccountId, x.Ordinal }).IsUnique();
		});

		model.Entity<ChangeStreamState>(e =>
		{
			e.HasKey(x => x.Id);
			e.HasOne<Account>()
				.WithMany()
				.HasForeignKey(x => x.AccountId)
				.OnDelete(DeleteBehavior.Cascade);
			e.HasOne<Mailbox>()
				.WithMany()
				.HasForeignKey(x => x.MailboxId)
				.OnDelete(DeleteBehavior.Cascade);

			// One stream per scope. MailboxId is null for Gmail, whose stream is
			// account-wide; SQLite's NULL-distinct semantics would not enforce that on its
			// own, so the account-scoped row gets a filtered unique index of its own.
			e.HasIndex(x => new { x.AccountId, x.MailboxId }).IsUnique();
			e.HasIndex(x => x.AccountId)
				.IsUnique()
				.HasFilter("\"MailboxId\" IS NULL");

			e.Property(x => x.CursorState).HasJsonConversion();
		});
	}

	private static void ConfigureMutations(ModelBuilder model)
	{
		model.Entity<MutationItem>(e =>
		{
			e.HasKey(x => x.Id);
			e.HasOne<Account>()
				.WithMany()
				.HasForeignKey(x => x.AccountId)
				.OnDelete(DeleteBehavior.Cascade);

			// No FK to Message: a message row may become a tombstone while mutation state
			// still references it, and physical deletion is a garbage-collection decision
			// based on references rather than something the mutation worker performs (§6).
			e.HasIndex(x => x.MessageId);

			// Total ordering per (AccountId, MessageId). The unique index is what makes
			// sequence assignment safe under concurrent enqueue: two items that both believe
			// they are next collide here rather than both being written.
			e.HasIndex(x => new
			{
				x.AccountId,
				x.MessageId,
				x.Sequence,
			})
				.IsUnique();

			// The claim query: eligible heads for one account.
			e.HasIndex(x => new { x.AccountId, x.State });

			e.Ignore(x => x.IsTerminal);
		});

		model.Entity<MutationExecutionAttempt>(e =>
		{
			e.HasKey(x => x.Id);
			e.HasOne<Account>()
				.WithMany()
				.HasForeignKey(x => x.AccountId)
				.OnDelete(DeleteBehavior.Cascade);

			// The recovery sweep queries from attempts, not from item states.
			e.HasIndex(x => new { x.State, x.ResultPersistedAt });

			e.HasOne<OutboxItem>()
				.WithMany()
				.HasForeignKey(x => x.OutboxItemId)
				.OnDelete(DeleteBehavior.Cascade);
		});

		model.Entity<MutationExecutionAttemptItem>(e =>
		{
			e.HasKey(x => new { x.AttemptId, x.MutationItemId });
			e.HasOne<MutationExecutionAttempt>()
				.WithMany(x => x.Items)
				.HasForeignKey(x => x.AttemptId)
				.OnDelete(DeleteBehavior.Cascade);
			e.HasOne<MutationItem>()
				.WithMany()
				.HasForeignKey(x => x.MutationItemId)
				.OnDelete(DeleteBehavior.Cascade);
		});

		model.Entity<OutboxItem>(e =>
		{
			e.HasKey(x => x.Id);
			e.HasOne<Account>()
				.WithMany()
				.HasForeignKey(x => x.AccountId)
				.OnDelete(DeleteBehavior.Cascade);

			// No FK to Draft: the draft is deleted once the message is sent, and the outbox
			// row outlives it as the record of what happened.
			e.HasIndex(x => new { x.AccountId, x.Status });

			// Reconciliation of an ambiguous send searches by this, so it must be findable.
			e.HasIndex(x => x.StableMessageId).IsUnique();
		});

		model.Entity<MessagePendingChange>(e =>
		{
			e.HasKey(x => x.Id);
			e.HasOne<MutationItem>()
				.WithMany()
				.HasForeignKey(x => x.MutationItemId)
				.OnDelete(DeleteBehavior.Cascade);

			// One desired value per message per field. Two pending values for one field
			// would be two answers to what the user asked for.
			e.HasIndex(x => new { x.MessageId, x.Field }).IsUnique();
		});
	}

	private static void ConfigureComposition(ModelBuilder model)
	{
		model.Entity<Draft>(e =>
		{
			e.HasKey(x => x.Id);
			e.HasOne<Account>()
				.WithMany()
				.HasForeignKey(x => x.AccountId)
				.OnDelete(DeleteBehavior.Cascade);
			e.HasOne<SendIdentity>()
				.WithMany()
				.HasForeignKey(x => x.SendIdentityId)
				.OnDelete(DeleteBehavior.Restrict);

			e.Property(x => x.To).HasAddressListConversion();
			e.Property(x => x.Cc).HasAddressListConversion();
			e.Property(x => x.Bcc).HasAddressListConversion();
			e.Property(x => x.Attachments).HasJsonConversion();
		});
	}

	private static void ConfigureCalendar(ModelBuilder model)
	{
		model.Entity<Calendar>(e =>
		{
			e.HasKey(x => x.Id);
			e.HasOne<Account>()
				.WithMany()
				.HasForeignKey(x => x.AccountId)
				.OnDelete(DeleteBehavior.Cascade);
			e.HasIndex(x => new { x.AccountId, x.ProviderCalendarId }).IsUnique();
		});

		model.Entity<CalendarEvent>(e =>
		{
			e.HasKey(x => x.Id);
			e.HasOne<Calendar>()
				.WithMany()
				.HasForeignKey(x => x.CalendarId)
				.OnDelete(DeleteBehavior.Cascade);

			e.Property(x => x.Organizer).HasJsonConversion();
			e.Property(x => x.Attendees).HasJsonConversion();
			e.Property(x => x.Reminders).HasJsonConversion();
			e.Property(x => x.RecurrenceRules).HasJsonConversion();
			e.Property(x => x.RecurrenceDates).HasJsonConversion();
			e.Property(x => x.ExceptionDates).HasJsonConversion();

			e.HasIndex(x => new { x.CalendarId, x.ProviderEventId }).IsUnique();

			// Invites are matched against events by iCalendar UID.
			e.HasIndex(x => x.ICalUid);
		});
	}

	private static void ConfigureNotifications(ModelBuilder model)
	{
		model.Entity<NotificationRecord>(e =>
		{
			e.HasKey(x => x.Id);
			e.HasOne<Account>()
				.WithMany()
				.HasForeignKey(x => x.AccountId)
				.OnDelete(DeleteBehavior.Cascade);
			e.HasOne<Message>()
				.WithMany()
				.HasForeignKey(x => x.MessageId)
				.OnDelete(DeleteBehavior.Cascade);

			// One notification per message per kind, no matter how many times eligibility is
			// evaluated for it — the unique constraint is what makes "insert, ignore a
			// conflict" safe rather than merely usually-safe. Filtered because a row recorded
			// from staging has no message id yet, and SQLite's own NULL-is-distinct behaviour
			// would otherwise let a filtered-out NULL slip past anyway — stated explicitly so
			// it reads as a decision, not an oversight.
			e.HasIndex(x => new { x.AccountId, x.MessageId, x.Kind })
				.IsUnique()
				.HasFilter("\"MessageId\" IS NOT NULL");

			// The equivalent guard while a notification is still identified only by the
			// provider's own stable id, before replay resolves it to a local message (§3).
			e.HasIndex(x => new { x.AccountId, x.ProviderStableId, x.Kind })
				.IsUnique()
				.HasFilter("\"ProviderStableId\" IS NOT NULL");

			// Redispatch-pending-at-startup scans this; ordering by DateTimeOffset in SQL
			// does not work; SQLite Id ordering is bar the point here regardless (§8, §9).
			e.HasIndex(x => x.DeliveredAt);
		});
	}

	private static void ConfigureExport(ModelBuilder model)
	{
		model.Entity<ExportJob>(e =>
		{
			e.HasKey(x => x.Id);
			e.HasOne<Account>().WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Cascade);

			// Startup reconciliation scans for jobs still in progress (§6 table).
			e.HasIndex(x => x.Status);
		});
	}
}
