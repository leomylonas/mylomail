using Hangfire.States;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MyloMail.Api.Credentials;
using MyloMail.Api.Domain;
using MyloMail.Api.Persistence;
using MyloMail.Api.Providers;
using MyloMail.Api.Scheduling;
using MyloMail.Api.Tests.Fakes;
using MyloMail.Api.Tests.Sync;
using Xunit;

namespace MyloMail.Api.Tests.Scheduling;

/// <summary>Bulk `.eml` export (§13 Export): folder recreation, progress, cancel, resume.</summary>
public sealed class ExportJobsTests : IAsyncLifetime
{
	private SyncHarness harness = null!;
	private string destination = null!;

	public async Task InitializeAsync()
	{
		harness = await SyncHarness.CreateAsync(ProviderShapes.Imap(ImapCapabilityTier.QResync));
		destination = Path.Combine(Path.GetTempPath(), "mylomail-export-tests", Guid.NewGuid().ToString("n"));
	}

	public async Task DisposeAsync()
	{
		await harness.DisposeAsync();
		if (Directory.Exists(destination))
		{
			Directory.Delete(destination, recursive: true);
		}
	}

	[Fact]
	public async Task Export_recreates_the_folder_hierarchy_and_writes_each_occurrence()
	{
		await SeedAsync(inboxMessages: 2, sentMessages: 1);

		var exportId = await harness.UsingAsync(scope =>
			StartAllAsync(scope)
		);
		await harness.UsingAsync(scope =>
			scope.GetRequiredService<ConnectivityMonitor>().ReportAsync(false)
		);
		await harness.UsingAsync(scope => scope.GetRequiredService<ExportJobs>().RunBatchAsync(exportId));

		var job = await harness.UsingAsync(scope =>
			scope.GetRequiredService<MyloMailDbContext>().ExportJobs.SingleAsync(j => j.Id == exportId)
		);

		Assert.Equal(ExportJobStatus.Completed, job.Status);
		Assert.Equal(3, job.TotalCount);
		Assert.Equal(3, job.WrittenCount);
		Assert.Equal(2, Directory.GetFiles(Path.Combine(destination, "INBOX"), "*.eml").Length);
		Assert.Single(Directory.GetFiles(Path.Combine(destination, "Sent"), "*.eml"));
	}

	[Theory]
	[InlineData(AuthState.NeedsReauth)]
	[InlineData(AuthState.Error)]
	public async Task Cached_export_remains_available_while_provider_access_is_paused(
		AuthState authState
	)
	{
		await SeedAsync(inboxMessages: 1, sentMessages: 0);
		await harness.UsingAsync(async scope =>
		{
			var context = scope.GetRequiredService<MyloMailDbContext>();
			var account = await context.Accounts.SingleAsync(a => a.Id == harness.Account.Id);
			account.AuthState = authState;
			await context.SaveChangesAsync();
		});

		var exportId = await harness.UsingAsync(scope =>
			StartAllAsync(scope)
		);
		await harness.UsingAsync(scope =>
			scope.GetRequiredService<ExportJobs>().RunBatchAsync(exportId)
		);

		var job = await harness.UsingAsync(scope =>
			scope.GetRequiredService<MyloMailDbContext>().ExportJobs.SingleAsync(j => j.Id == exportId)
		);
		Assert.Equal(ExportJobStatus.Completed, job.Status);
		Assert.Equal(1, job.WrittenCount);
	}

	[Fact]
	public async Task A_cancel_request_stops_the_next_batch_without_finishing()
	{
		await SeedAsync(inboxMessages: 2, sentMessages: 0);

		var exportId = await harness.UsingAsync(scope =>
			StartAllAsync(scope)
		);
		await harness.UsingAsync(scope => scope.GetRequiredService<ExportJobs>().RequestCancelAsync(exportId));
		await harness.UsingAsync(scope => scope.GetRequiredService<ExportJobs>().RunBatchAsync(exportId));

		var job = await harness.UsingAsync(scope =>
			scope.GetRequiredService<MyloMailDbContext>().ExportJobs.SingleAsync(j => j.Id == exportId)
		);

		Assert.Equal(ExportJobStatus.Cancelled, job.Status);
		Assert.Equal(0, job.WrittenCount);
	}

	[Fact]
	public async Task A_batch_failure_can_resume_from_where_it_left_off()
	{
		await SeedAsync(inboxMessages: 2, sentMessages: 0);

		var exportId = await harness.UsingAsync(scope =>
			StartAllAsync(scope)
		);

		// Simulates the process dying mid-export: progress already saved is kept, and a second
		// call — standing in for startup reconciliation re-enqueuing the same job — picks up
		// from ResumeToken rather than re-writing what is already on disk.
		await harness.UsingAsync(async scope =>
		{
			var context = scope.GetRequiredService<MyloMailDbContext>();
			var job = await context.ExportJobs.SingleAsync(j => j.Id == exportId);
			job.ResumeToken = 1;
			job.WrittenCount = 1;
			await context.SaveChangesAsync();
		});

		await harness.UsingAsync(scope => scope.GetRequiredService<ExportJobs>().RunBatchAsync(exportId));

		var completed = await harness.UsingAsync(scope =>
			scope.GetRequiredService<MyloMailDbContext>().ExportJobs.SingleAsync(j => j.Id == exportId)
		);

		Assert.Equal(ExportJobStatus.Completed, completed.Status);
		Assert.Equal(2, completed.WrittenCount);
		Assert.Single(Directory.GetFiles(Path.Combine(destination, "INBOX"), "*.eml"));
	}

	[Fact]
	public async Task Mail_arriving_during_the_export_is_included_once_and_the_export_ends_when_nothing_new_is_left()
	{
		await SeedAsync(inboxMessages: 2, sentMessages: 0);

		var exportId = await harness.UsingAsync(scope => StartAllAsync(scope));

		// A `MessageMailbox.Id` is a random v4 GUID, not a sequential one (§1 identity rule), which
		// is why the manifest is frozen rather than re-derived by offset on every batch. Mail that
		// arrives after that is picked up by a catch-up pass over the chosen folders instead.
		await SeedAsync(inboxMessages: 1, sentMessages: 0);

		await RunToEndAsync(exportId);

		var job = await LoadAsync(exportId);
		Assert.Equal(ExportJobStatus.Completed, job.Status);
		Assert.Equal(3, job.TotalCount);
		Assert.Equal(3, job.WrittenCount);
		Assert.Equal(1, job.CatchUpPasses);
		Assert.NotNull(job.CompletedAt);
		Assert.Equal(3, Directory.GetFiles(Path.Combine(destination, "INBOX"), "*.eml").Length);
	}

	[Fact]
	public async Task Only_the_chosen_folders_are_exported()
	{
		await SeedAsync(inboxMessages: 2, sentMessages: 3);
		var sentId = await harness.UsingAsync(scope =>
			scope.GetRequiredService<MyloMailDbContext>().Mailboxes.Where(m => m.Name == "Sent").Select(m => m.Id).SingleAsync()
		);

		var exportId = await harness.UsingAsync(scope =>
			scope.GetRequiredService<ExportJobs>().StartAsync(harness.Account.Id, [sentId], ExportFormat.EmlFolders, ExportFileNaming.MessageId, destination)
		);
		await RunToEndAsync(exportId);

		Assert.Equal(3, (await LoadAsync(exportId)).WrittenCount);
		Assert.False(Directory.Exists(Path.Combine(destination, "INBOX")));
		Assert.Equal(3, Directory.GetFiles(Path.Combine(destination, "Sent"), "*.eml").Length);
	}

	[Fact]
	public async Task Mbox_export_writes_one_file_per_folder_with_subfolders_beside_it_in_a_sbd_directory()
	{
		await SeedAsync(inboxMessages: 2, sentMessages: 1);
		await harness.UsingAsync(async scope =>
		{
			var context = scope.GetRequiredService<MyloMailDbContext>();
			var inbox = await context.Mailboxes.SingleAsync(m => m.Name == "INBOX");
			var sent = await context.Mailboxes.SingleAsync(m => m.Name == "Sent");
			sent.ParentId = inbox.Id;
			await context.SaveChangesAsync();
		});

		var exportId = await harness.UsingAsync(scope => StartAllAsync(scope, ExportFormat.Mbox));
		await RunToEndAsync(exportId);

		var inboxFile = await File.ReadAllTextAsync(Path.Combine(destination, "INBOX"));
		var sentFile = await File.ReadAllTextAsync(Path.Combine(destination, "INBOX.sbd", "Sent"));
		Assert.Equal(2, MessageCount(inboxFile));
		Assert.StartsWith("From ", sentFile, StringComparison.Ordinal);
		Assert.Contains("Body", sentFile);
	}

	[Fact]
	public async Task A_batch_that_appended_to_an_mbox_but_never_saved_does_not_repeat_its_messages_on_resume()
	{
		await SeedAsync(inboxMessages: 30, sentMessages: 0);
		var exportId = await harness.UsingAsync(scope => StartAllAsync(scope, ExportFormat.Mbox));

		await harness.UsingAsync(scope => scope.GetRequiredService<ExportJobs>().RunBatchAsync(exportId));
		var path = Path.Combine(destination, "INBOX");
		// The next batch's worth, appended and then lost: the process died before the position was saved.
		await File.AppendAllTextAsync(path, "From x Mon Jan  1 00:00:00 2024\nSubject: stray\n\nstray\n\n");

		await RunToEndAsync(exportId);

		var text = await File.ReadAllTextAsync(path);
		Assert.DoesNotContain("stray", text);
		Assert.Equal(30, MessageCount(text));
	}

	[Fact]
	public async Task Files_named_by_date_and_subject_keep_two_messages_with_the_same_name_apart()
	{
		await SeedAsync(inboxMessages: 2, sentMessages: 0);
		await harness.UsingAsync(async scope =>
		{
			var context = scope.GetRequiredService<MyloMailDbContext>();
			foreach (var message in context.Messages)
			{
				message.Subject = "Invoice: Q3 / final?";
				message.ReceivedAt = new DateTimeOffset(2026, 10, 21, 15, 42, 0, TimeSpan.Zero);
			}

			// Different messages, not one stored twice.
			foreach (var raw in context.MessageRaws)
			{
				raw.Content = [.. raw.Content, .. raw.MessageId.ToByteArray()];
			}

			await context.SaveChangesAsync();
		});

		var exportId = await harness.UsingAsync(scope => StartAllAsync(scope, ExportFormat.EmlFolders, ExportFileNaming.DateAndSubject));
		await RunToEndAsync(exportId);

		var names = Directory.GetFiles(Path.Combine(destination, "INBOX")).Select(Path.GetFileName).Order().ToList();
		Assert.Equal(2, names.Count);
		Assert.Contains("2026-10-21 1542 - Invoice_ Q3 _ final_.eml", names);
		Assert.Contains(names, name => name!.StartsWith("2026-10-21 1542 - Invoice_ Q3 _ final_ [", StringComparison.Ordinal));
	}

	[Fact]
	public async Task Stale_fetch_failure_keeps_manifest_position_until_content_is_refetched()
	{
		var (mailboxId, messageId) = await SeedUnfetchedAsync();
		var exportId = await harness.UsingAsync(scope =>
			StartAllAsync(scope)
		);
		harness.Provider.BeforeFetchRawMessageReturnAsync = async _ =>
		{
			await harness.UsingAsync(async scope =>
			{
				var context = scope.GetRequiredService<MyloMailDbContext>();
				var mailbox = await context.Mailboxes.SingleAsync(m => m.Id == mailboxId);
				mailbox.TopologyGeneration++;
				await context.SaveChangesAsync();
			});
			throw new InvalidOperationException("Old occurrence disappeared.");
		};

		await harness.UsingAsync(scope => scope.GetRequiredService<ExportJobs>().RunBatchAsync(exportId));

		var deferred = await harness.UsingAsync(scope =>
			scope.GetRequiredService<MyloMailDbContext>().ExportJobs.SingleAsync(j => j.Id == exportId)
		);
		Assert.Equal(ExportJobStatus.Running, deferred.Status);
		Assert.Equal(0, deferred.ResumeToken);
		Assert.Equal(0, deferred.WrittenCount);

		harness.Provider.BeforeFetchRawMessageReturnAsync = null;
		await harness.UsingAsync(scope => scope.GetRequiredService<ExportJobs>().RunBatchAsync(exportId));

		var completed = await harness.UsingAsync(scope =>
			scope.GetRequiredService<MyloMailDbContext>().ExportJobs.SingleAsync(j => j.Id == exportId)
		);
		Assert.Equal(ExportJobStatus.Completed, completed.Status);
		Assert.Equal(1, completed.ResumeToken);
		Assert.Equal(1, completed.WrittenCount);
		Assert.True(File.Exists(Path.Combine(destination, "INBOX", $"{messageId:N}.eml")));
	}

	[Fact]
	public async Task Provider_throttling_keeps_the_manifest_position_and_uses_the_exact_delay()
	{
		var (_, messageId) = await SeedUnfetchedAsync();
		var exportId = await harness.UsingAsync(scope =>
			StartAllAsync(scope)
		);
		var jobs = await harness.UsingAsync(scope =>
			Task.FromResult(
				(RecordingJobClient)scope.GetRequiredService<Hangfire.IBackgroundJobClient>()
			)
		);
		jobs.Created.Clear();
		jobs.States.Clear();
		var retryAfter = TimeSpan.FromSeconds(17);
		var before = DateTime.UtcNow;
		harness.Provider.FailFetchRawMessageWith(
			new ProviderThrottledException(retryAfter, "Wait.")
		);

		await harness.UsingAsync(scope =>
			scope.GetRequiredService<ExportJobs>().RunBatchAsync(exportId)
		);

		await harness.UsingAsync(async scope =>
		{
			var context = scope.GetRequiredService<MyloMailDbContext>();
			var export = await context.ExportJobs.SingleAsync(job => job.Id == exportId);
			var content = await context.MessageContentStates.SingleAsync(
				state => state.MessageId == messageId
			);
			Assert.Equal(ExportJobStatus.Running, export.Status);
			Assert.Equal(0, export.ResumeToken);
			Assert.Equal(0, content.Attempts);
			Assert.Equal(
				retryAfter,
				scope.GetRequiredService<AccountGate>().Delay(harness.Account.Id)
			);
		});
		var scheduled = Assert.IsType<ScheduledState>(Assert.Single(jobs.States));
		Assert.InRange(
			scheduled.EnqueueAt,
			before.Add(retryAfter).AddSeconds(-1),
			DateTime.UtcNow.Add(retryAfter).AddSeconds(1)
		);
	}

	[Theory]
	[InlineData(false, AuthState.NeedsReauth)]
	[InlineData(true, AuthState.Error)]
	public async Task Account_access_failure_keeps_export_resumable_without_retrying(
		bool providerNotConfigured,
		AuthState expectedState
	)
	{
		await SeedUnfetchedAsync();
		var exportId = await harness.UsingAsync(scope =>
			StartAllAsync(scope)
		);
		Exception failure = providerNotConfigured
			? new ProviderNotConfiguredException(ProviderType.Imap, "credential")
			: new ProviderAuthenticationException("reauthenticate");
		harness.Provider.FailFetchRawMessageWith(failure);

		await harness.UsingAsync(async scope =>
		{
			var recorder =
				(RecordingJobClient)scope.GetRequiredService<Hangfire.IBackgroundJobClient>();
			recorder.Created.Clear();
			recorder.States.Clear();

			await scope.GetRequiredService<ExportJobs>().RunBatchAsync(exportId);

			var context = scope.GetRequiredService<MyloMailDbContext>();
			var export = await context.ExportJobs.SingleAsync(job => job.Id == exportId);
			var account = await context.Accounts.SingleAsync(a => a.Id == harness.Account.Id);
			Assert.Equal(ExportJobStatus.Running, export.Status);
			Assert.Equal(0, export.ResumeToken);
			Assert.Equal(expectedState, account.AuthState);
			Assert.Empty(recorder.Created);
		});
		Assert.Equal(expectedState, Assert.Single(harness.Events.AccountStatuses).AuthState);
	}

	[Fact]
	public async Task Credential_store_failure_keeps_export_position_and_schedules_retry()
	{
		await SeedUnfetchedAsync();
		var exportId = await harness.UsingAsync(scope =>
			StartAllAsync(scope)
		);
		harness.Provider.FailFetchRawMessageWith(
			new CredentialStoreUnavailableException("the keyring is locked")
		);

		await harness.UsingAsync(async scope =>
		{
			var recorder =
				(RecordingJobClient)scope.GetRequiredService<Hangfire.IBackgroundJobClient>();
			recorder.Created.Clear();
			recorder.States.Clear();

			await scope.GetRequiredService<ExportJobs>().RunBatchAsync(exportId);

			var context = scope.GetRequiredService<MyloMailDbContext>();
			var export = await context.ExportJobs.SingleAsync(job => job.Id == exportId);
			var account = await context.Accounts.SingleAsync(a => a.Id == harness.Account.Id);
			Assert.Equal(ExportJobStatus.Running, export.Status);
			Assert.Equal(0, export.ResumeToken);
			Assert.Equal(AuthState.CredentialStoreUnavailable, account.AuthState);
			Assert.IsType<ScheduledState>(Assert.Single(recorder.States));
		});
	}

	private async Task<(Guid MailboxId, Guid MessageId)> SeedUnfetchedAsync()
	{
		var providerMailbox = harness.Provider.AddMailbox("INBOX", SpecialUse.Inbox);
		var messageId = Guid.NewGuid();
		var providerOccurrenceId = harness.Provider.SeedMessage(
			"INBOX",
			messageId,
			DateTimeOffset.UnixEpoch
		);
		providerMailbox.Messages[providerOccurrenceId].RawBytes =
			"From: author@example.test\r\nTo: recipient@example.test\r\nSubject: Export\r\n\r\nBody"u8.ToArray();
		var mailboxId = Guid.NewGuid();

		await harness.UsingAsync(async scope =>
		{
			var context = scope.GetRequiredService<MyloMailDbContext>();
			context.Mailboxes.Add(
				new Mailbox
				{
					Id = mailboxId,
					AccountId = harness.Account.Id,
					ProviderMailboxId = "INBOX",
					Name = "INBOX",
					SpecialUse = SpecialUse.Inbox,
				}
			);
			context.Messages.Add(
				new Message
				{
					Id = messageId,
					AccountId = harness.Account.Id,
					Occurrences =
					[
						new MessageMailbox
						{
							Id = Guid.NewGuid(),
							MailboxId = mailboxId,
							ProviderOccurrenceId = providerOccurrenceId,
						},
					],
				}
			);
			context.MessageContentStates.Add(
				new MessageContentState
				{
					MessageId = messageId,
					Status = ContentStatus.Queued,
				}
			);
			await context.SaveChangesAsync();
		});

		return (mailboxId, messageId);
	}

	[Fact]
	public async Task Recent_exports_are_listed_newest_first()
	{
		await SeedAsync(inboxMessages: 1, sentMessages: 0);
		var first = await harness.UsingAsync(scope => StartAllAsync(scope));
		var second = await harness.UsingAsync(scope => StartAllAsync(scope));
		// The test clock stands still, so the order is made visible.
		await harness.UsingAsync(async scope =>
		{
			var context = scope.GetRequiredService<MyloMailDbContext>();
			var later = await context.ExportJobs.SingleAsync(j => j.Id == second);
			later.CreatedAt = later.CreatedAt.AddDays(1);
			await context.SaveChangesAsync();
		});

		var recent = await harness.UsingAsync(scope => scope.GetRequiredService<ExportJobs>().GetRecentAsync(10));

		Assert.Equal([second, first], recent.Select(job => job.Id).ToList());
	}

	private string PstPath => Path.Combine(destination, "export.pst");

	private static List<(string Folder, string Subject)> PstContents(string path)
	{
		using var session = OfficeIMO.Email.Store.EmailStoreSession.Open(path);
		return
		[
			.. session
				.EnumerateItems()
				.Select(reference =>
				{
					var item = session.ReadItem(reference);
					return (item.FolderId ?? string.Empty, item.Document.Subject ?? string.Empty);
				}),
		];
	}

	[Fact]
	public async Task Pst_export_writes_every_message_into_its_folder_and_leaves_no_working_files()
	{
		await SeedAsync(inboxMessages: 2, sentMessages: 1);
		var exportId = await harness.UsingAsync(scope => StartAllAsync(scope, ExportFormat.Pst));

		await RunToEndAsync(exportId);

		Assert.Equal(ExportJobStatus.Completed, (await LoadAsync(exportId)).Status);
		var items = PstContents(PstPath);
		Assert.Equal(3, items.Count);
		Assert.Equal([1, 2], items.GroupBy(i => i.Folder).Select(g => g.Count()).Order().ToList());
		Assert.False(File.Exists(PstPath + ".mylomail-export"));
	}

	[Fact]
	public async Task A_pst_export_takes_its_position_from_the_writers_checkpoint_not_the_database()
	{
		await SeedAsync(inboxMessages: 30, sentMessages: 0);
		var exportId = await harness.UsingAsync(scope => StartAllAsync(scope, ExportFormat.Pst));
		await harness.UsingAsync(scope => scope.GetRequiredService<ExportJobs>().RunBatchAsync(exportId));

		// The database claims everything was written, as if a later batch saved its position and the
		// file then lost that work; the checkpoint says only the first batch is in the file.
		await harness.UsingAsync(async scope =>
		{
			var context = scope.GetRequiredService<MyloMailDbContext>();
			var job = await context.ExportJobs.SingleAsync(j => j.Id == exportId);
			job.ResumeToken = 30;
			job.WrittenCount = 30;
			await context.SaveChangesAsync();
		});
		await RunToEndAsync(exportId);

		Assert.Equal(30, PstContents(PstPath).Count);
		Assert.Equal(30, (await LoadAsync(exportId)).WrittenCount);
	}

	[Fact]
	public async Task Cancelling_a_pst_export_removes_what_it_had_started()
	{
		await SeedAsync(inboxMessages: 30, sentMessages: 0);
		var exportId = await harness.UsingAsync(scope => StartAllAsync(scope, ExportFormat.Pst));
		await harness.UsingAsync(scope => scope.GetRequiredService<ExportJobs>().RunBatchAsync(exportId));
		Assert.True(File.Exists(PstPath + ".mylomail-export"));

		await harness.UsingAsync(scope => scope.GetRequiredService<ExportJobs>().RequestCancelAsync(exportId));
		await harness.UsingAsync(scope => scope.GetRequiredService<ExportJobs>().RunBatchAsync(exportId));

		Assert.Equal(ExportJobStatus.Cancelled, (await LoadAsync(exportId)).Status);
		Assert.False(File.Exists(PstPath + ".mylomail-export"));
		Assert.False(File.Exists(PstPath));
	}

	private static int MessageCount(string mbox) =>
		System.Text.RegularExpressions.Regex.Matches(mbox, "^From ", System.Text.RegularExpressions.RegexOptions.Multiline).Count;

	private async Task<Guid> StartAllAsync(
		IServiceProvider scope,
		ExportFormat format = ExportFormat.EmlFolders,
		ExportFileNaming naming = ExportFileNaming.MessageId
	)
	{
		var context = scope.GetRequiredService<MyloMailDbContext>();
		var account = await context.Accounts.SingleAsync();
		var mailboxIds = await context.Mailboxes.Where(m => m.AccountId == account.Id).Select(m => m.Id).ToListAsync();
		return await scope
			.GetRequiredService<ExportJobs>()
			.StartAsync(account.Id, mailboxIds, format, naming, format == ExportFormat.Pst ? PstPath : destination);
	}

	private async Task RunToEndAsync(Guid exportId)
	{
		for (var i = 0; i < 50 && (await LoadAsync(exportId)).Status == ExportJobStatus.Running; i++)
		{
			await harness.UsingAsync(scope => scope.GetRequiredService<ExportJobs>().RunBatchAsync(exportId));
		}
	}

	private Task<ExportJob> LoadAsync(Guid exportId) =>
		harness.UsingAsync(scope => scope.GetRequiredService<MyloMailDbContext>().ExportJobs.AsNoTracking().SingleAsync(j => j.Id == exportId));

	private async Task SeedAsync(int inboxMessages, int sentMessages)
	{
		await harness.UsingAsync(async scope =>
		{
			var context = scope.GetRequiredService<MyloMailDbContext>();

			async Task<Mailbox> MailboxAsync(string providerMailboxId, SpecialUse specialUse)
			{
				var existing = await context.Mailboxes.FirstOrDefaultAsync(m =>
					m.AccountId == harness.Account.Id && m.ProviderMailboxId == providerMailboxId
				);
				if (existing is not null)
				{
					return existing;
				}

				var mailbox = new Mailbox
				{
					Id = Guid.NewGuid(),
					AccountId = harness.Account.Id,
					ProviderMailboxId = providerMailboxId,
					Name = providerMailboxId,
					SpecialUse = specialUse,
				};
				context.Mailboxes.Add(mailbox);
				return mailbox;
			}

			var inbox = await MailboxAsync("INBOX", SpecialUse.Inbox);
			var sent = await MailboxAsync("Sent", SpecialUse.Sent);

			async Task AddMessagesAsync(Mailbox mailbox, int count)
			{
				var next = await context.MessageMailboxes.CountAsync(o => o.MailboxId == mailbox.Id);
				for (var i = 0; i < count; i++)
				{
					var index = next + i;
					var messageId = Guid.NewGuid();
					context.Messages.Add(
						new Message { Id = messageId, AccountId = harness.Account.Id, Subject = $"m{index}" }
					);
					context.MessageMailboxes.Add(
						new MessageMailbox
						{
							Id = Guid.NewGuid(),
							MessageId = messageId,
							MailboxId = mailbox.Id,
							ProviderOccurrenceId = $"{index}",
						}
					);
					context.MessageRaws.Add(
						new MessageRaw { MessageId = messageId, Content = "Subject: test\r\n\r\nBody"u8.ToArray() }
					);
				}
			}

			await AddMessagesAsync(inbox, inboxMessages);
			await AddMessagesAsync(sent, sentMessages);

			await context.SaveChangesAsync();
		});
	}
}
