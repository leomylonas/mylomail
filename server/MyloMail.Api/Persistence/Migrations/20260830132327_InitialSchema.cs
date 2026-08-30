using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MyloMail.Api.Persistence.Migrations
{
	/// <inheritdoc />
	public partial class InitialSchema : Migration
	{
		/// <inheritdoc />
		protected override void Up(MigrationBuilder migrationBuilder)
		{
			migrationBuilder.CreateTable(
				name: "Accounts",
				columns: table => new
				{
					Id = table.Column<Guid>(type: "TEXT", nullable: false),
					DisplayName = table.Column<string>(type: "TEXT", nullable: false),
					ProviderType = table.Column<int>(type: "INTEGER", nullable: false),
					AuthState = table.Column<int>(type: "INTEGER", nullable: false),
					IsEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
					LastAuthError = table.Column<string>(type: "TEXT", nullable: true),
					Color = table.Column<string>(type: "TEXT", nullable: false),
					SortOrder = table.Column<int>(type: "INTEGER", nullable: false),
					ProviderConfig = table.Column<string>(type: "TEXT", nullable: true),
					PollIntervalSeconds = table.Column<int>(type: "INTEGER", nullable: false),
					PollingEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
					InitialSyncMode = table.Column<int>(type: "INTEGER", nullable: false),
					InitialSyncBoundValue = table.Column<int>(type: "INTEGER", nullable: true),
					UndoSendDelaySeconds = table.Column<int>(type: "INTEGER", nullable: false),
					NotificationsEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
					AttachmentSizeLimitOverride = table.Column<int>(type: "INTEGER", nullable: true),
					CertificateTrustMode = table.Column<int>(type: "INTEGER", nullable: false)
				},
				constraints: table =>
				{
					table.PrimaryKey("PK_Accounts", x => x.Id);
				});

			migrationBuilder.CreateTable(
				name: "AppSettings",
				columns: table => new
				{
					Id = table.Column<int>(type: "INTEGER", nullable: false),
					CloseBehavior = table.Column<int>(type: "INTEGER", nullable: false),
					TelemetryEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
					OtelEndpoint = table.Column<string>(type: "TEXT", nullable: true),
					MailtoPromptDismissed = table.Column<bool>(type: "INTEGER", nullable: false),
					PanelLayout = table.Column<string>(type: "TEXT", nullable: true),
					WindowBoundsJson = table.Column<string>(type: "TEXT", nullable: true),
					Theme = table.Column<int>(type: "INTEGER", nullable: false),
					AttachmentTempCleanupOnStartup = table.Column<bool>(type: "INTEGER", nullable: false)
				},
				constraints: table =>
				{
					table.PrimaryKey("PK_AppSettings", x => x.Id);
					table.CheckConstraint("CK_AppSettings_SingleRow", "\"Id\" = 1");
				});

			migrationBuilder.CreateTable(
				name: "AccountTrustedCertificates",
				columns: table => new
				{
					Id = table.Column<Guid>(type: "TEXT", nullable: false),
					AccountId = table.Column<Guid>(type: "TEXT", nullable: false),
					Thumbprint = table.Column<string>(type: "TEXT", nullable: false)
				},
				constraints: table =>
				{
					table.PrimaryKey("PK_AccountTrustedCertificates", x => x.Id);
					table.ForeignKey(
						name: "FK_AccountTrustedCertificates_Accounts_AccountId",
						column: x => x.AccountId,
						principalTable: "Accounts",
						principalColumn: "Id",
						onDelete: ReferentialAction.Cascade);
				});

			migrationBuilder.CreateTable(
				name: "Calendars",
				columns: table => new
				{
					Id = table.Column<Guid>(type: "TEXT", nullable: false),
					AccountId = table.Column<Guid>(type: "TEXT", nullable: false),
					ProviderCalendarId = table.Column<string>(type: "TEXT", nullable: false),
					Name = table.Column<string>(type: "TEXT", nullable: false),
					Colour = table.Column<string>(type: "TEXT", nullable: true),
					IsDefault = table.Column<bool>(type: "INTEGER", nullable: false)
				},
				constraints: table =>
				{
					table.PrimaryKey("PK_Calendars", x => x.Id);
					table.ForeignKey(
						name: "FK_Calendars_Accounts_AccountId",
						column: x => x.AccountId,
						principalTable: "Accounts",
						principalColumn: "Id",
						onDelete: ReferentialAction.Cascade);
				});

			migrationBuilder.CreateTable(
				name: "Mailboxes",
				columns: table => new
				{
					Id = table.Column<Guid>(type: "TEXT", nullable: false),
					AccountId = table.Column<Guid>(type: "TEXT", nullable: false),
					ProviderMailboxId = table.Column<string>(type: "TEXT", nullable: true),
					ParentId = table.Column<Guid>(type: "TEXT", nullable: true),
					Name = table.Column<string>(type: "TEXT", nullable: false),
					SpecialUse = table.Column<int>(type: "INTEGER", nullable: false),
					IsSubscribed = table.Column<bool>(type: "INTEGER", nullable: false),
					LocalSortOrder = table.Column<int>(type: "INTEGER", nullable: false),
					InitialSyncModeOverride = table.Column<int>(type: "INTEGER", nullable: true),
					InitialSyncBoundValueOverride = table.Column<int>(type: "INTEGER", nullable: true),
					ProviderTotalCount = table.Column<int>(type: "INTEGER", nullable: true),
					ProviderUnreadCount = table.Column<int>(type: "INTEGER", nullable: true),
					TopologyGeneration = table.Column<int>(type: "INTEGER", nullable: false)
				},
				constraints: table =>
				{
					table.PrimaryKey("PK_Mailboxes", x => x.Id);
					table.ForeignKey(
						name: "FK_Mailboxes_Accounts_AccountId",
						column: x => x.AccountId,
						principalTable: "Accounts",
						principalColumn: "Id",
						onDelete: ReferentialAction.Cascade);
					table.ForeignKey(
						name: "FK_Mailboxes_Mailboxes_ParentId",
						column: x => x.ParentId,
						principalTable: "Mailboxes",
						principalColumn: "Id",
						onDelete: ReferentialAction.Restrict);
				});

			migrationBuilder.CreateTable(
				name: "MailboxTopologySyncStates",
				columns: table => new
				{
					AccountId = table.Column<Guid>(type: "TEXT", nullable: false),
					Cursor = table.Column<string>(type: "TEXT", nullable: true),
					LastReconciledAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
					LastError = table.Column<string>(type: "TEXT", nullable: true)
				},
				constraints: table =>
				{
					table.PrimaryKey("PK_MailboxTopologySyncStates", x => x.AccountId);
					table.ForeignKey(
						name: "FK_MailboxTopologySyncStates_Accounts_AccountId",
						column: x => x.AccountId,
						principalTable: "Accounts",
						principalColumn: "Id",
						onDelete: ReferentialAction.Cascade);
				});

			migrationBuilder.CreateTable(
				name: "Messages",
				columns: table => new
				{
					Id = table.Column<Guid>(type: "TEXT", nullable: false),
					AccountId = table.Column<Guid>(type: "TEXT", nullable: false),
					ProviderStableId = table.Column<string>(type: "TEXT", nullable: true),
					MessageIdHeader = table.Column<string>(type: "TEXT", nullable: true),
					InReplyToHeader = table.Column<string>(type: "TEXT", nullable: true),
					ReferencesHeader = table.Column<string>(type: "TEXT", nullable: true),
					ReplyToAddresses = table.Column<string>(type: "TEXT", nullable: false),
					SenderAddress = table.Column<string>(type: "TEXT", nullable: true),
					ThreadId = table.Column<string>(type: "TEXT", nullable: true),
					From = table.Column<string>(type: "TEXT", nullable: false),
					To = table.Column<string>(type: "TEXT", nullable: false),
					Cc = table.Column<string>(type: "TEXT", nullable: false),
					Bcc = table.Column<string>(type: "TEXT", nullable: false),
					Subject = table.Column<string>(type: "TEXT", nullable: false),
					Snippet = table.Column<string>(type: "TEXT", nullable: false),
					ReceivedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
					IsRead = table.Column<bool>(type: "INTEGER", nullable: false),
					IsFlagged = table.Column<bool>(type: "INTEGER", nullable: false),
					IsDraft = table.Column<bool>(type: "INTEGER", nullable: false),
					IsAnswered = table.Column<bool>(type: "INTEGER", nullable: false),
					HasNonInlineAttachments = table.Column<bool>(type: "INTEGER", nullable: false),
					SizeEstimate = table.Column<long>(type: "INTEGER", nullable: true),
					RawFetched = table.Column<bool>(type: "INTEGER", nullable: false)
				},
				constraints: table =>
				{
					table.PrimaryKey("PK_Messages", x => x.Id);
					table.ForeignKey(
						name: "FK_Messages_Accounts_AccountId",
						column: x => x.AccountId,
						principalTable: "Accounts",
						principalColumn: "Id",
						onDelete: ReferentialAction.Cascade);
				});

			migrationBuilder.CreateTable(
				name: "SendIdentities",
				columns: table => new
				{
					Id = table.Column<Guid>(type: "TEXT", nullable: false),
					AccountId = table.Column<Guid>(type: "TEXT", nullable: false),
					DisplayName = table.Column<string>(type: "TEXT", nullable: false),
					EmailAddress = table.Column<string>(type: "TEXT", nullable: false),
					SignatureHtml = table.Column<string>(type: "TEXT", nullable: true),
					IsDefault = table.Column<bool>(type: "INTEGER", nullable: false)
				},
				constraints: table =>
				{
					table.PrimaryKey("PK_SendIdentities", x => x.Id);
					table.ForeignKey(
						name: "FK_SendIdentities_Accounts_AccountId",
						column: x => x.AccountId,
						principalTable: "Accounts",
						principalColumn: "Id",
						onDelete: ReferentialAction.Cascade);
				});

			migrationBuilder.CreateTable(
				name: "CalendarEvents",
				columns: table => new
				{
					Id = table.Column<Guid>(type: "TEXT", nullable: false),
					CalendarId = table.Column<Guid>(type: "TEXT", nullable: false),
					ProviderEventId = table.Column<string>(type: "TEXT", nullable: false),
					ICalUid = table.Column<string>(type: "TEXT", nullable: false),
					ProviderRevision = table.Column<string>(type: "TEXT", nullable: true),
					Sequence = table.Column<int>(type: "INTEGER", nullable: false),
					Title = table.Column<string>(type: "TEXT", nullable: false),
					Location = table.Column<string>(type: "TEXT", nullable: true),
					Description = table.Column<string>(type: "TEXT", nullable: true),
					Start = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
					End = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
					StartTimeZoneId = table.Column<string>(type: "TEXT", nullable: true),
					EndTimeZoneId = table.Column<string>(type: "TEXT", nullable: true),
					IsAllDay = table.Column<bool>(type: "INTEGER", nullable: false),
					Organizer = table.Column<string>(type: "TEXT", nullable: true),
					Attendees = table.Column<string>(type: "TEXT", nullable: false),
					Status = table.Column<int>(type: "INTEGER", nullable: false),
					Reminders = table.Column<string>(type: "TEXT", nullable: false),
					RecurrenceRules = table.Column<string>(type: "TEXT", nullable: false),
					RecurrenceDates = table.Column<string>(type: "TEXT", nullable: false),
					ExceptionDates = table.Column<string>(type: "TEXT", nullable: false),
					RecurrenceMasterId = table.Column<Guid>(type: "TEXT", nullable: true),
					RecurrenceId = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
					SyncConflict = table.Column<bool>(type: "INTEGER", nullable: false)
				},
				constraints: table =>
				{
					table.PrimaryKey("PK_CalendarEvents", x => x.Id);
					table.ForeignKey(
						name: "FK_CalendarEvents_Calendars_CalendarId",
						column: x => x.CalendarId,
						principalTable: "Calendars",
						principalColumn: "Id",
						onDelete: ReferentialAction.Cascade);
				});

			migrationBuilder.CreateTable(
				name: "ChangeStreamStates",
				columns: table => new
				{
					Id = table.Column<Guid>(type: "TEXT", nullable: false),
					AccountId = table.Column<Guid>(type: "TEXT", nullable: false),
					MailboxId = table.Column<Guid>(type: "TEXT", nullable: true),
					CursorKind = table.Column<int>(type: "INTEGER", nullable: false),
					CursorState = table.Column<string>(type: "TEXT", nullable: true),
					BaselineEstablishedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
					LastSyncedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
					LastError = table.Column<string>(type: "TEXT", nullable: true)
				},
				constraints: table =>
				{
					table.PrimaryKey("PK_ChangeStreamStates", x => x.Id);
					table.ForeignKey(
						name: "FK_ChangeStreamStates_Accounts_AccountId",
						column: x => x.AccountId,
						principalTable: "Accounts",
						principalColumn: "Id",
						onDelete: ReferentialAction.Cascade);
					table.ForeignKey(
						name: "FK_ChangeStreamStates_Mailboxes_MailboxId",
						column: x => x.MailboxId,
						principalTable: "Mailboxes",
						principalColumn: "Id",
						onDelete: ReferentialAction.Cascade);
				});

			migrationBuilder.CreateTable(
				name: "ImapMailboxMetadata",
				columns: table => new
				{
					MailboxId = table.Column<Guid>(type: "TEXT", nullable: false),
					FullName = table.Column<string>(type: "TEXT", nullable: false),
					HierarchyDelimiter = table.Column<char>(type: "TEXT", nullable: false),
					NamespacePrefix = table.Column<string>(type: "TEXT", nullable: true)
				},
				constraints: table =>
				{
					table.PrimaryKey("PK_ImapMailboxMetadata", x => x.MailboxId);
					table.ForeignKey(
						name: "FK_ImapMailboxMetadata_Mailboxes_MailboxId",
						column: x => x.MailboxId,
						principalTable: "Mailboxes",
						principalColumn: "Id",
						onDelete: ReferentialAction.Cascade);
				});

			migrationBuilder.CreateTable(
				name: "IntegrityReconciliationStates",
				columns: table => new
				{
					MailboxId = table.Column<Guid>(type: "TEXT", nullable: false),
					LastReconciledAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
					LastError = table.Column<string>(type: "TEXT", nullable: true)
				},
				constraints: table =>
				{
					table.PrimaryKey("PK_IntegrityReconciliationStates", x => x.MailboxId);
					table.ForeignKey(
						name: "FK_IntegrityReconciliationStates_Mailboxes_MailboxId",
						column: x => x.MailboxId,
						principalTable: "Mailboxes",
						principalColumn: "Id",
						onDelete: ReferentialAction.Cascade);
				});

			migrationBuilder.CreateTable(
				name: "MailboxCoverageStates",
				columns: table => new
				{
					MailboxId = table.Column<Guid>(type: "TEXT", nullable: false),
					Status = table.Column<int>(type: "INTEGER", nullable: false),
					MessagesFetched = table.Column<int>(type: "INTEGER", nullable: false),
					EstimatedTotal = table.Column<int>(type: "INTEGER", nullable: true),
					ResumeToken = table.Column<string>(type: "TEXT", nullable: true),
					StartedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
					LastError = table.Column<string>(type: "TEXT", nullable: true)
				},
				constraints: table =>
				{
					table.PrimaryKey("PK_MailboxCoverageStates", x => x.MailboxId);
					table.ForeignKey(
						name: "FK_MailboxCoverageStates_Mailboxes_MailboxId",
						column: x => x.MailboxId,
						principalTable: "Mailboxes",
						principalColumn: "Id",
						onDelete: ReferentialAction.Cascade);
				});

			migrationBuilder.CreateTable(
				name: "Attachments",
				columns: table => new
				{
					Id = table.Column<Guid>(type: "TEXT", nullable: false),
					MessageId = table.Column<Guid>(type: "TEXT", nullable: false),
					PartSpecifier = table.Column<string>(type: "TEXT", nullable: false),
					RawVersion = table.Column<int>(type: "INTEGER", nullable: false),
					Filename = table.Column<string>(type: "TEXT", nullable: false),
					MimeType = table.Column<string>(type: "TEXT", nullable: false),
					Size = table.Column<long>(type: "INTEGER", nullable: false),
					ContentId = table.Column<string>(type: "TEXT", nullable: true),
					IsInline = table.Column<bool>(type: "INTEGER", nullable: false)
				},
				constraints: table =>
				{
					table.PrimaryKey("PK_Attachments", x => x.Id);
					table.ForeignKey(
						name: "FK_Attachments_Messages_MessageId",
						column: x => x.MessageId,
						principalTable: "Messages",
						principalColumn: "Id",
						onDelete: ReferentialAction.Cascade);
				});

			migrationBuilder.CreateTable(
				name: "MessageBodies",
				columns: table => new
				{
					MessageId = table.Column<Guid>(type: "TEXT", nullable: false),
					TextBody = table.Column<string>(type: "TEXT", nullable: true),
					HtmlBody = table.Column<string>(type: "TEXT", nullable: true)
				},
				constraints: table =>
				{
					table.PrimaryKey("PK_MessageBodies", x => x.MessageId);
					table.ForeignKey(
						name: "FK_MessageBodies_Messages_MessageId",
						column: x => x.MessageId,
						principalTable: "Messages",
						principalColumn: "Id",
						onDelete: ReferentialAction.Cascade);
				});

			migrationBuilder.CreateTable(
				name: "MessageContentStates",
				columns: table => new
				{
					MessageId = table.Column<Guid>(type: "TEXT", nullable: false),
					Status = table.Column<int>(type: "INTEGER", nullable: false),
					RawVersion = table.Column<int>(type: "INTEGER", nullable: false),
					Attempts = table.Column<int>(type: "INTEGER", nullable: false),
					LastError = table.Column<string>(type: "TEXT", nullable: true)
				},
				constraints: table =>
				{
					table.PrimaryKey("PK_MessageContentStates", x => x.MessageId);
					table.ForeignKey(
						name: "FK_MessageContentStates_Messages_MessageId",
						column: x => x.MessageId,
						principalTable: "Messages",
						principalColumn: "Id",
						onDelete: ReferentialAction.Cascade);
				});

			migrationBuilder.CreateTable(
				name: "MessageHeaders",
				columns: table => new
				{
					MessageId = table.Column<Guid>(type: "TEXT", nullable: false),
					Headers = table.Column<string>(type: "TEXT", nullable: false)
				},
				constraints: table =>
				{
					table.PrimaryKey("PK_MessageHeaders", x => x.MessageId);
					table.ForeignKey(
						name: "FK_MessageHeaders_Messages_MessageId",
						column: x => x.MessageId,
						principalTable: "Messages",
						principalColumn: "Id",
						onDelete: ReferentialAction.Cascade);
				});

			migrationBuilder.CreateTable(
				name: "MessageMailboxes",
				columns: table => new
				{
					Id = table.Column<Guid>(type: "TEXT", nullable: false),
					MessageId = table.Column<Guid>(type: "TEXT", nullable: false),
					MailboxId = table.Column<Guid>(type: "TEXT", nullable: false),
					ProviderOccurrenceId = table.Column<string>(type: "TEXT", nullable: false),
					ImapModSeq = table.Column<long>(type: "INTEGER", nullable: true)
				},
				constraints: table =>
				{
					table.PrimaryKey("PK_MessageMailboxes", x => x.Id);
					table.ForeignKey(
						name: "FK_MessageMailboxes_Mailboxes_MailboxId",
						column: x => x.MailboxId,
						principalTable: "Mailboxes",
						principalColumn: "Id",
						onDelete: ReferentialAction.Cascade);
					table.ForeignKey(
						name: "FK_MessageMailboxes_Messages_MessageId",
						column: x => x.MessageId,
						principalTable: "Messages",
						principalColumn: "Id",
						onDelete: ReferentialAction.Cascade);
				});

			migrationBuilder.CreateTable(
				name: "MessageRaws",
				columns: table => new
				{
					MessageId = table.Column<Guid>(type: "TEXT", nullable: false),
					Content = table.Column<byte[]>(type: "BLOB", nullable: false)
				},
				constraints: table =>
				{
					table.PrimaryKey("PK_MessageRaws", x => x.MessageId);
					table.ForeignKey(
						name: "FK_MessageRaws_Messages_MessageId",
						column: x => x.MessageId,
						principalTable: "Messages",
						principalColumn: "Id",
						onDelete: ReferentialAction.Cascade);
				});

			migrationBuilder.CreateTable(
				name: "MessageSearchContents",
				columns: table => new
				{
					RowId = table.Column<long>(type: "INTEGER", nullable: false)
						.Annotation("Sqlite:Autoincrement", true),
					MessageId = table.Column<Guid>(type: "TEXT", nullable: false),
					Subject = table.Column<string>(type: "TEXT", nullable: false),
					BodyText = table.Column<string>(type: "TEXT", nullable: false),
					FromAddresses = table.Column<string>(type: "TEXT", nullable: false),
					ToAddresses = table.Column<string>(type: "TEXT", nullable: false),
					CcAddresses = table.Column<string>(type: "TEXT", nullable: false)
				},
				constraints: table =>
				{
					table.PrimaryKey("PK_MessageSearchContents", x => x.RowId);
					table.ForeignKey(
						name: "FK_MessageSearchContents_Messages_MessageId",
						column: x => x.MessageId,
						principalTable: "Messages",
						principalColumn: "Id",
						onDelete: ReferentialAction.Cascade);
				});

			migrationBuilder.CreateTable(
				name: "Drafts",
				columns: table => new
				{
					Id = table.Column<Guid>(type: "TEXT", nullable: false),
					AccountId = table.Column<Guid>(type: "TEXT", nullable: false),
					SendIdentityId = table.Column<Guid>(type: "TEXT", nullable: false),
					InReplyToMessageId = table.Column<Guid>(type: "TEXT", nullable: true),
					To = table.Column<string>(type: "TEXT", nullable: false),
					Cc = table.Column<string>(type: "TEXT", nullable: false),
					Bcc = table.Column<string>(type: "TEXT", nullable: false),
					Subject = table.Column<string>(type: "TEXT", nullable: false),
					BodyHtml = table.Column<string>(type: "TEXT", nullable: false),
					Attachments = table.Column<string>(type: "TEXT", nullable: false),
					SavedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
					ProviderDraftId = table.Column<string>(type: "TEXT", nullable: true),
					ProviderRevision = table.Column<string>(type: "TEXT", nullable: true)
				},
				constraints: table =>
				{
					table.PrimaryKey("PK_Drafts", x => x.Id);
					table.ForeignKey(
						name: "FK_Drafts_Accounts_AccountId",
						column: x => x.AccountId,
						principalTable: "Accounts",
						principalColumn: "Id",
						onDelete: ReferentialAction.Cascade);
					table.ForeignKey(
						name: "FK_Drafts_SendIdentities_SendIdentityId",
						column: x => x.SendIdentityId,
						principalTable: "SendIdentities",
						principalColumn: "Id",
						onDelete: ReferentialAction.Restrict);
				});

			migrationBuilder.CreateIndex(
				name: "IX_AccountTrustedCertificates_AccountId_Thumbprint",
				table: "AccountTrustedCertificates",
				columns: new[] { "AccountId", "Thumbprint" },
				unique: true);

			migrationBuilder.CreateIndex(
				name: "IX_Attachments_MessageId",
				table: "Attachments",
				column: "MessageId");

			migrationBuilder.CreateIndex(
				name: "IX_CalendarEvents_CalendarId_ProviderEventId",
				table: "CalendarEvents",
				columns: new[] { "CalendarId", "ProviderEventId" },
				unique: true);

			migrationBuilder.CreateIndex(
				name: "IX_CalendarEvents_ICalUid",
				table: "CalendarEvents",
				column: "ICalUid");

			migrationBuilder.CreateIndex(
				name: "IX_Calendars_AccountId_ProviderCalendarId",
				table: "Calendars",
				columns: new[] { "AccountId", "ProviderCalendarId" },
				unique: true);

			migrationBuilder.CreateIndex(
				name: "IX_ChangeStreamStates_AccountId",
				table: "ChangeStreamStates",
				column: "AccountId",
				unique: true,
				filter: "\"MailboxId\" IS NULL");

			migrationBuilder.CreateIndex(
				name: "IX_ChangeStreamStates_AccountId_MailboxId",
				table: "ChangeStreamStates",
				columns: new[] { "AccountId", "MailboxId" },
				unique: true);

			migrationBuilder.CreateIndex(
				name: "IX_ChangeStreamStates_MailboxId",
				table: "ChangeStreamStates",
				column: "MailboxId");

			migrationBuilder.CreateIndex(
				name: "IX_Drafts_AccountId",
				table: "Drafts",
				column: "AccountId");

			migrationBuilder.CreateIndex(
				name: "IX_Drafts_SendIdentityId",
				table: "Drafts",
				column: "SendIdentityId");

			migrationBuilder.CreateIndex(
				name: "IX_Mailboxes_AccountId_ProviderMailboxId",
				table: "Mailboxes",
				columns: new[] { "AccountId", "ProviderMailboxId" },
				unique: true);

			migrationBuilder.CreateIndex(
				name: "IX_Mailboxes_ParentId",
				table: "Mailboxes",
				column: "ParentId");

			migrationBuilder.CreateIndex(
				name: "IX_MessageContentStates_Status",
				table: "MessageContentStates",
				column: "Status");

			migrationBuilder.CreateIndex(
				name: "IX_MessageMailboxes_MailboxId_ProviderOccurrenceId",
				table: "MessageMailboxes",
				columns: new[] { "MailboxId", "ProviderOccurrenceId" },
				unique: true);

			migrationBuilder.CreateIndex(
				name: "IX_MessageMailboxes_MessageId_MailboxId",
				table: "MessageMailboxes",
				columns: new[] { "MessageId", "MailboxId" },
				unique: true);

			migrationBuilder.CreateIndex(
				name: "IX_Messages_AccountId_MessageIdHeader",
				table: "Messages",
				columns: new[] { "AccountId", "MessageIdHeader" });

			migrationBuilder.CreateIndex(
				name: "IX_Messages_AccountId_ProviderStableId",
				table: "Messages",
				columns: new[] { "AccountId", "ProviderStableId" },
				unique: true);

			migrationBuilder.CreateIndex(
				name: "IX_Messages_AccountId_ReceivedAt",
				table: "Messages",
				columns: new[] { "AccountId", "ReceivedAt" });

			migrationBuilder.CreateIndex(
				name: "IX_MessageSearchContents_MessageId",
				table: "MessageSearchContents",
				column: "MessageId",
				unique: true);

			migrationBuilder.CreateIndex(
				name: "IX_SendIdentities_AccountId_Default",
				table: "SendIdentities",
				column: "AccountId",
				unique: true,
				filter: "\"IsDefault\" = 1");

			// The FTS5 index over MessageSearchContents (§8). External-content, so the
			// text is stored once; content_rowid is the INTEGER key that FTS5 requires and
			// a Guid cannot supply.
			//
			// No triggers: they are fiddly against external-content tables and invisible to
			// EF Core, so insert, update, delete and rebuild are explicit upserts issued in
			// the same transaction as the content write.
			migrationBuilder.Sql(
				"""
                CREATE VIRTUAL TABLE "MessageSearchIndex" USING fts5(
                    "Subject",
                    "BodyText",
                    "FromAddresses",
                    "ToAddresses",
                    "CcAddresses",
                    content='MessageSearchContents',
                    content_rowid='RowId'
                );
                """);
		}

		/// <inheritdoc />
		protected override void Down(MigrationBuilder migrationBuilder)
		{
			migrationBuilder.Sql("DROP TABLE IF EXISTS \"MessageSearchIndex\";");

			migrationBuilder.DropTable(
				name: "AccountTrustedCertificates");

			migrationBuilder.DropTable(
				name: "AppSettings");

			migrationBuilder.DropTable(
				name: "Attachments");

			migrationBuilder.DropTable(
				name: "CalendarEvents");

			migrationBuilder.DropTable(
				name: "ChangeStreamStates");

			migrationBuilder.DropTable(
				name: "Drafts");

			migrationBuilder.DropTable(
				name: "ImapMailboxMetadata");

			migrationBuilder.DropTable(
				name: "IntegrityReconciliationStates");

			migrationBuilder.DropTable(
				name: "MailboxCoverageStates");

			migrationBuilder.DropTable(
				name: "MailboxTopologySyncStates");

			migrationBuilder.DropTable(
				name: "MessageBodies");

			migrationBuilder.DropTable(
				name: "MessageContentStates");

			migrationBuilder.DropTable(
				name: "MessageHeaders");

			migrationBuilder.DropTable(
				name: "MessageMailboxes");

			migrationBuilder.DropTable(
				name: "MessageRaws");

			migrationBuilder.DropTable(
				name: "MessageSearchContents");

			migrationBuilder.DropTable(
				name: "Calendars");

			migrationBuilder.DropTable(
				name: "SendIdentities");

			migrationBuilder.DropTable(
				name: "Mailboxes");

			migrationBuilder.DropTable(
				name: "Messages");

			migrationBuilder.DropTable(
				name: "Accounts");
		}
	}
}
