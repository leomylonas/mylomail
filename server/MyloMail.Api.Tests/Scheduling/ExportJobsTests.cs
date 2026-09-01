using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
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
			scope.GetRequiredService<ExportJobs>().StartAsync(harness.Account.Id, destination)
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

	[Fact]
	public async Task A_cancel_request_stops_the_next_batch_without_finishing()
	{
		await SeedAsync(inboxMessages: 2, sentMessages: 0);

		var exportId = await harness.UsingAsync(scope =>
			scope.GetRequiredService<ExportJobs>().StartAsync(harness.Account.Id, destination)
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
			scope.GetRequiredService<ExportJobs>().StartAsync(harness.Account.Id, destination)
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
	public async Task Mail_arriving_after_the_manifest_is_frozen_is_neither_skipped_nor_duplicated()
	{
		await SeedAsync(inboxMessages: 2, sentMessages: 0);

		var exportId = await harness.UsingAsync(scope =>
			scope.GetRequiredService<ExportJobs>().StartAsync(harness.Account.Id, destination)
		);

		// A `MessageMailbox.Id` is a random v4 GUID, not a sequential one (§1 identity rule),
		// so a plain `ORDER BY Id OFFSET n` re-derived against the live table on every batch
		// would have this row land anywhere relative to already-frozen positions — including
		// before the export's current point, silently pushing an unwritten row past
		// `TotalCount`. Freezing the manifest at start removes that dependency entirely: mail
		// arriving mid-export is simply outside this run, to be picked up by the next one.
		await SeedAsync(inboxMessages: 1, sentMessages: 0);

		await harness.UsingAsync(scope => scope.GetRequiredService<ExportJobs>().RunBatchAsync(exportId));

		var job = await harness.UsingAsync(scope =>
			scope.GetRequiredService<MyloMailDbContext>().ExportJobs.SingleAsync(j => j.Id == exportId)
		);

		Assert.Equal(ExportJobStatus.Completed, job.Status);
		Assert.Equal(2, job.TotalCount);
		Assert.Equal(2, job.WrittenCount);
		Assert.Equal(2, Directory.GetFiles(Path.Combine(destination, "INBOX"), "*.eml").Length);
	}

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
