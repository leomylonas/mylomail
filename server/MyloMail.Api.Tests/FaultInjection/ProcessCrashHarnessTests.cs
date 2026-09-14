using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MimeKit;
using MyloMail.Api.Domain;
using MyloMail.Api.FaultInjection;
using MyloMail.Api.Persistence;
using MyloMail.Api.Providers.Contracts;
using MyloMail.Api.Sync;
using MyloMail.Api.Tests.Persistence;
using Xunit;

namespace MyloMail.Api.Tests.FaultInjection;

[Trait("Category", "FaultInjection")]
[Trait("Category", "Deep")]
public sealed class ProcessCrashHarnessTests
{
	[Fact]
	public async Task A_mid_attachment_download_process_kill_recovers_after_a_real_restart()
	{
		await using var database = new TestDatabase();
		await database.MigrateAsync();
		var (messageId, attachmentId, payload) = await SeedAttachmentAsync(database);
		await using var harness = new ProcessFaultHarness(database.Directory);
		var first = await harness.StartAsync(
			FaultPoints.AttachmentDownloadMidTransfer,
			waitForAcknowledgement: true
		);
		var interrupted = ObserveInterruptedDownloadAsync(first, messageId, attachmentId);
		await harness.WaitForCrashAsync(first);
		Assert.InRange(await interrupted, 1, payload.Length - 1);
		Assert.Equal(
			FaultPoints.AttachmentDownloadMidTransfer,
			await File.ReadAllTextAsync(first.MarkerPath)
		);

		var restarted = await harness.StartAsync();
		Assert.NotEqual(first.Process.Id, restarted.Process.Id);
		Assert.Equal(payload, await DownloadAsync(restarted, messageId, attachmentId));
	}
	[Fact]
	public async Task Tombstone_and_search_index_deletion_roll_back_together_after_a_real_restart()
	{
		await using var database = new TestDatabase();
		await database.MigrateAsync();
		var messageId = await SeedCollectibleTombstoneAsync(database);
		await using var harness = new ProcessFaultHarness(database.Directory);

		var first = await harness.StartAsync(FaultPoints.TombstoneAfterDeleteBeforeCommit);
		await harness.WaitForCrashAsync(first);
		await AssertMessageAndSearchRowAsync(database, messageId, expected: true);

		var restarted = await harness.StartAsync();
		Assert.NotEqual(first.Process.Id, restarted.Process.Id);
		await WaitForMessageAndSearchDeletionAsync(database, messageId);
		await database.AssertSearchIndexIsIntactAsync();
	}

	[Fact]
	public async Task Staged_history_application_rolls_back_and_replays_after_a_real_restart()
	{
		await using var database = new TestDatabase();
		await database.MigrateAsync();
		var (messageId, stagedId) = await SeedStagedPermanentDeletionAsync(database);
		await using var harness = new ProcessFaultHarness(database.Directory);

		var first = await harness.StartAsync(FaultPoints.SyncPageAfterApplyBeforeCommit);
		await harness.WaitForCrashAsync(first);
		await AssertStagedDeletionStateAsync(
			database,
			messageId,
			stagedId,
			membershipExists: true,
			stagedExists: true
		);

		var restarted = await harness.StartAsync();
		Assert.NotEqual(first.Process.Id, restarted.Process.Id);
		await WaitForStagedDeletionAsync(database, messageId, stagedId);
	}

	[Fact]
	public async Task Account_removal_intent_survives_a_process_kill_before_database_deletion()
	{
		await using var database = new TestDatabase();
		await database.MigrateAsync();
		var accountId = await SeedRemovableAccountAsync(database);
		await using var harness = new ProcessFaultHarness(database.Directory);

		var first = await harness.StartAsync(
			FaultPoints.AccountRemovalAfterIntentBeforeDatabaseDelete
		);
		using (var client = CreateClient(first))
		{
			var request = client.DeleteAsync($"accounts/{accountId}?force=true");
			await harness.WaitForCrashAsync(first);
			await Assert.ThrowsAnyAsync<Exception>(async () => await request);
		}

		await using (var scope = database.CreateScope())
		{
			var context = scope.ServiceProvider.GetRequiredService<MyloMailDbContext>();
			Assert.False((await context.Accounts.SingleAsync(account => account.Id == accountId)).IsEnabled);
			Assert.True(await context.AccountCredentialCleanups.AnyAsync(cleanup => cleanup.AccountId == accountId));
		}

		var restarted = await harness.StartAsync();
		Assert.NotEqual(first.Process.Id, restarted.Process.Id);
		await WaitForAccountRemovalAsync(database, accountId);
	}

	private static async Task<Guid> SeedRemovableAccountAsync(TestDatabase database)
	{
		var accountId = Guid.NewGuid();
		await using var scope = database.CreateScope();
		var context = scope.ServiceProvider.GetRequiredService<MyloMailDbContext>();
		context.Accounts.Add(new Account
		{
			Id = accountId,
			DisplayName = "Process-killed removal",
			ProviderType = ProviderType.Imap,
			AuthState = AuthState.Connected,
			IsEnabled = true,
			PollingEnabled = false,
		});
		await context.SaveChangesAsync();
		return accountId;
	}

	private static async Task WaitForAccountRemovalAsync(TestDatabase database, Guid accountId)
	{
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
		while (true)
		{
			await using var scope = database.CreateScope();
			var context = scope.ServiceProvider.GetRequiredService<MyloMailDbContext>();
			if (
				!await context.Accounts.AnyAsync(account => account.Id == accountId, timeout.Token)
				&& !await context.AccountCredentialCleanups.AnyAsync(
					cleanup => cleanup.AccountId == accountId,
					timeout.Token
				)
			)
			{
				return;
			}
			await Task.Delay(TimeSpan.FromMilliseconds(50), timeout.Token);
		}
	}


	private static async Task<byte[]> DownloadAsync(
		RunningBackend backend,
		Guid messageId,
		Guid attachmentId
	)
	{
		using var client = CreateClient(backend);
		return await client.GetByteArrayAsync(
			$"messages/{messageId}/attachments/{attachmentId}"
		);
	}

	private static async Task<int> ObserveInterruptedDownloadAsync(
		RunningBackend backend,
		Guid messageId,
		Guid attachmentId
	)
	{
		using var client = CreateClient(backend);
		using var response = await client.GetAsync(
			$"messages/{messageId}/attachments/{attachmentId}",
			HttpCompletionOption.ResponseHeadersRead
		);
		response.EnsureSuccessStatusCode();
		await using var content = await response.Content.ReadAsStreamAsync();
		var buffer = new byte[4096];
		var received = 0;
		try
		{
			while (await content.ReadAsync(buffer) is var count && count != 0)
			{
				received += count;
				if (received == count)
				{
					File.WriteAllText(backend.AcknowledgementPath!, "observed");
				}
			}
		}
		catch (IOException) when (received > 0)
		{
			// Kestrel advertised the complete Content-Length, so the hard process kill can
			// surface as a truncated-body exception after the flushed prefix is observed.
		}

		return received;
	}

	private static HttpClient CreateClient(RunningBackend backend)
	{
		var client = new HttpClient
		{
			BaseAddress = new Uri($"http://127.0.0.1:{backend.Port}"),
		};
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
			"Bearer",
			backend.LaunchToken
		);
		return client;
	}
	private static async Task<(Guid MessageId, Guid StagedId)> SeedStagedPermanentDeletionAsync(
		TestDatabase database
	)
	{
		var accountId = Guid.NewGuid();
		var mailboxId = Guid.NewGuid();
		var messageId = Guid.NewGuid();
		var stagedId = Guid.NewGuid();
		await using var scope = database.CreateScope();
		var context = scope.ServiceProvider.GetRequiredService<MyloMailDbContext>();
		context.Accounts.Add(
			new Account
			{
				Id = accountId,
				DisplayName = "Staged replay process fixture",
				ProviderType = ProviderType.Gmail,
				AuthState = AuthState.Connected,
				IsEnabled = true,
				PollingEnabled = false,
			}
		);
		context.Mailboxes.Add(
			new Mailbox
			{
				Id = mailboxId,
				AccountId = accountId,
				ProviderMailboxId = "INBOX",
				Name = "Inbox",
				SpecialUse = SpecialUse.Inbox,
				IsSubscribed = true,
			}
		);
		context.MailboxCoverageStates.Add(
			new MailboxCoverageState
			{
				MailboxId = mailboxId,
				Status = CoverageStatus.Covered,
			}
		);
		context.Messages.Add(
			new Message
			{
				Id = messageId,
				AccountId = accountId,
				ProviderStableId = "deleted-provider-message",
				Subject = "Permanently deleted while coverage ran",
				ReceivedAt = DateTimeOffset.UtcNow,
			}
		);
		context.MessageMailboxes.Add(
			new MessageMailbox
			{
				Id = Guid.NewGuid(),
				MessageId = messageId,
				MailboxId = mailboxId,
				ProviderOccurrenceId = "deleted-provider-message",
			}
		);
		context.StagedChangeEvents.Add(
			new StagedChangeEvent
			{
				Id = stagedId,
				AccountId = accountId,
				Ordinal = 1,
				Payload = SyncPagePayload.Serialize(
					new SyncResult(
						null,
						null,
						[],
						[],
						[],
						["deleted-provider-message"]
					),
					[],
					GenerationSnapshot.From(
						new Dictionary<string, int> { ["INBOX"] = 0 }
					)
				),
				StagedAt = DateTimeOffset.UtcNow,
			}
		);
		await context.SaveChangesAsync();
		return (messageId, stagedId);
	}

	private static async Task AssertStagedDeletionStateAsync(
		TestDatabase database,
		Guid messageId,
		Guid stagedId,
		bool membershipExists,
		bool stagedExists
	)
	{
		await using var scope = database.CreateScope();
		var context = scope.ServiceProvider.GetRequiredService<MyloMailDbContext>();
		Assert.Equal(
			membershipExists,
			await context.MessageMailboxes.AnyAsync(occurrence => occurrence.MessageId == messageId)
		);
		Assert.Equal(
			stagedExists,
			await context.StagedChangeEvents.AnyAsync(staged => staged.Id == stagedId)
		);
	}

	private static async Task WaitForStagedDeletionAsync(
		TestDatabase database,
		Guid messageId,
		Guid stagedId
	)
	{
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
		while (true)
		{
			await using var scope = database.CreateScope();
			var context = scope.ServiceProvider.GetRequiredService<MyloMailDbContext>();
			if (!await context.MessageMailboxes.AnyAsync(
					occurrence => occurrence.MessageId == messageId,
					timeout.Token
				)
				&& !await context.StagedChangeEvents.AnyAsync(
					staged => staged.Id == stagedId,
					timeout.Token
				))
			{
				return;
			}
			await Task.Delay(20, timeout.Token);
		}
	}

	private static async Task<Guid> SeedCollectibleTombstoneAsync(TestDatabase database)
	{
		var accountId = Guid.NewGuid();
		var messageId = Guid.NewGuid();
		await using var scope = database.CreateScope();
		var context = scope.ServiceProvider.GetRequiredService<MyloMailDbContext>();
		context.Accounts.Add(
			new Account
			{
				Id = accountId,
				DisplayName = "Tombstone process fixture",
				ProviderType = ProviderType.Imap,
				IsEnabled = true,
				PollingEnabled = false,
			}
		);
		context.Messages.Add(
			new Message
			{
				Id = messageId,
				AccountId = accountId,
				Subject = "searchable tombstone",
				ReceivedAt = DateTimeOffset.UtcNow.AddHours(-1),
				OrphanedAt = DateTimeOffset.UtcNow.AddMinutes(-31),
			}
		);
		var search = new MessageSearchContent
		{
			MessageId = messageId,
			Subject = "searchable tombstone",
			BodyText = "durable searchable body",
			FromAddresses = "sender@example.org",
			ToAddresses = "reader@example.org",
		};
		context.MessageSearchContents.Add(search);
		await context.SaveChangesAsync();
		await context.Database.ExecuteSqlInterpolatedAsync(
			$"""
			INSERT INTO "MessageSearchIndex"("rowid", "Subject", "BodyText", "FromAddresses", "ToAddresses", "CcAddresses")
			VALUES ({search.RowId}, {search.Subject}, {search.BodyText}, {search.FromAddresses}, {search.ToAddresses}, {search.CcAddresses});
			"""
		);
		return messageId;
	}

	private static async Task AssertMessageAndSearchRowAsync(
		TestDatabase database,
		Guid messageId,
		bool expected
	)
	{
		await using var scope = database.CreateScope();
		var context = scope.ServiceProvider.GetRequiredService<MyloMailDbContext>();
		Assert.Equal(expected, await context.Messages.AnyAsync(message => message.Id == messageId));
		Assert.Equal(
			expected,
			await context.MessageSearchContents.AnyAsync(content => content.MessageId == messageId)
		);
	}

	private static async Task WaitForMessageAndSearchDeletionAsync(
		TestDatabase database,
		Guid messageId
	)
	{
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
		while (true)
		{
			await using var scope = database.CreateScope();
			var context = scope.ServiceProvider.GetRequiredService<MyloMailDbContext>();
			if (!await context.Messages.AnyAsync(message => message.Id == messageId, timeout.Token)
				&& !await context.MessageSearchContents.AnyAsync(
					content => content.MessageId == messageId,
					timeout.Token
				))
			{
				return;
			}
			await Task.Delay(20, timeout.Token);
		}
	}


	private static async Task<(Guid MessageId, Guid AttachmentId, byte[] Payload)> SeedAttachmentAsync(
		TestDatabase database
	)
	{
		var messageId = Guid.NewGuid();
		var attachmentId = Guid.NewGuid();
		var payload = Enumerable.Range(0, 32 * 1024).Select(value => (byte)(value % 251)).ToArray();
		var mime = new MimeMessage();
		mime.From.Add(MailboxAddress.Parse("sender@example.org"));
		mime.To.Add(MailboxAddress.Parse("reader@example.org"));
		var body = new BodyBuilder { TextBody = "body" };
		body.Attachments.Add("report.bin", payload, ContentType.Parse("application/octet-stream"));
		mime.Body = body.ToMessageBody();
		await using var rawStream = new MemoryStream();
		await mime.WriteToAsync(rawStream);

		var iterator = new MimeIterator(mime);
		string? partSpecifier = null;
		while (iterator.MoveNext())
		{
			if (iterator.Current is MimePart { IsAttachment: true })
			{
				partSpecifier = iterator.PathSpecifier;
				break;
			}
		}
		Assert.NotNull(partSpecifier);

		await using var scope = database.CreateScope();
		var context = scope.ServiceProvider.GetRequiredService<MyloMailDbContext>();
		var accountId = Guid.NewGuid();
		context.Accounts.Add(new Account
		{
			Id = accountId,
			DisplayName = "Process crash fixture",
			ProviderType = ProviderType.Imap,
			IsEnabled = false,
		});
		context.Messages.Add(new Message
		{
			Id = messageId,
			AccountId = accountId,
			ReceivedAt = DateTimeOffset.UtcNow,
		});
		context.MessageRaws.Add(new MessageRaw
		{
			MessageId = messageId,
			Content = rawStream.ToArray(),
		});
		context.MessageContentStates.Add(new MessageContentState
		{
			MessageId = messageId,
			RawVersion = 1,
		});
		context.Attachments.Add(new Attachment
		{
			Id = attachmentId,
			MessageId = messageId,
			PartSpecifier = partSpecifier,
			RawVersion = 1,
			Filename = "report.bin",
			MimeType = "application/octet-stream",
			Size = payload.Length,
		});
		await context.SaveChangesAsync();
		return (messageId, attachmentId, payload);
	}
}

internal sealed record RunningBackend(
	Process Process,
	int Port,
	string LaunchToken,
	string MarkerPath,
	string? AcknowledgementPath,
	Task<string> StandardError
);

internal sealed class ProcessFaultHarness : IAsyncDisposable
{
	private readonly string dataDirectory;
	private readonly List<RunningBackend> processes = [];

	public ProcessFaultHarness(string dataDirectory) =>
		this.dataDirectory = dataDirectory;

	public async Task<RunningBackend> StartAsync(
		string? faultPoint = null,
		bool waitForAcknowledgement = false
	)
	{
		var launchToken = Convert.ToHexString(Guid.NewGuid().ToByteArray());
		var marker = Path.Combine(dataDirectory, $"fault-{Guid.NewGuid():N}.marker");
		var acknowledgement = waitForAcknowledgement
			? Path.Combine(dataDirectory, $"fault-{Guid.NewGuid():N}.ack")
			: null;
		var start = new ProcessStartInfo("dotnet")
		{
			WorkingDirectory = AppContext.BaseDirectory,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
			RedirectStandardInput = true,
			CreateNoWindow = true,
		};
		start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "MyloMail.Api.dll"));
		start.Environment["DOTNET_ENVIRONMENT"] = "FaultInjection";
		start.Environment["ASPNETCORE_ENVIRONMENT"] = "FaultInjection";

		start.Environment.Remove("MYLOMAIL_RENDERER_PATH");
		start.Environment.Remove(ProcessFaultInjector.PointEnvironmentVariable);
		start.Environment.Remove(ProcessFaultInjector.MarkerEnvironmentVariable);
		start.Environment.Remove(ProcessFaultInjector.AcknowledgementEnvironmentVariable);
		start.Environment[ProcessFaultInjector.DataDirectoryEnvironmentVariable] =
			dataDirectory;
		if (faultPoint is not null)
		{
			start.Environment[ProcessFaultInjector.PointEnvironmentVariable] = faultPoint;
			start.Environment[ProcessFaultInjector.MarkerEnvironmentVariable] = marker;
			if (acknowledgement is not null)
			{
				start.Environment[ProcessFaultInjector.AcknowledgementEnvironmentVariable] =
					acknowledgement;
			}
		}

		var process = Process.Start(start)
			?? throw new InvalidOperationException("The backend process did not start.");
		await process.StandardInput.WriteLineAsync(
			JsonSerializer.Serialize(
				new
				{
					launchToken,
					masterPassword = "process-fault-password",
				}
			)
		);
		process.StandardInput.Close();
		var port = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
		var standardOutput = PumpOutputAsync(process.StandardOutput, port);
		var standardError = process.StandardError.ReadToEndAsync();
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
		var exited = process.WaitForExitAsync(timeout.Token);
		var completed = await Task.WhenAny(port.Task, exited);
		if (completed == exited)
		{
			await standardOutput;
			if (faultPoint is not null && File.Exists(marker))
			{
				var crashed = new RunningBackend(
					process,
					0,
					launchToken,
					marker,
					acknowledgement,
					standardError
				);
				processes.Add(crashed);
				return crashed;
			}
			throw new InvalidOperationException(
				$"Backend exited before reporting a port: {await standardError}"
			);
		}

		var boundPort = await port.Task.WaitAsync(timeout.Token);
		try
		{
			await WaitForHealthAsync(boundPort, launchToken, timeout.Token);
		}
		catch when (faultPoint is not null && File.Exists(marker))
		{
			await process.WaitForExitAsync(timeout.Token);
		}
		var backend = new RunningBackend(
			process,
			boundPort,
			launchToken,
			marker,
			acknowledgement,
			standardError
		);
		processes.Add(backend);
		return backend;
	}

	public async Task WaitForCrashAsync(RunningBackend backend)
	{
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
		while (!File.Exists(backend.MarkerPath))
		{
			if (backend.Process.HasExited)
			{
				throw new InvalidOperationException(
					$"Backend exited with code {backend.Process.ExitCode} before writing the fault marker: {await backend.StandardError}"
				);
			}
			await Task.Delay(20, timeout.Token);
		}
		await backend.Process.WaitForExitAsync(timeout.Token);
		Assert.NotEqual(0, backend.Process.ExitCode);
	}

	public async ValueTask DisposeAsync()
	{
		foreach (var backend in processes)
		{
			if (!backend.Process.HasExited)
			{
				backend.Process.Kill(entireProcessTree: true);
				await backend.Process.WaitForExitAsync();
			}
			await backend.StandardError;
			backend.Process.Dispose();
		}
	}

	private static async Task WaitForHealthAsync(
		int port,
		string launchToken,
		CancellationToken ct
	)
	{
		using var client = new HttpClient
		{
			BaseAddress = new Uri($"http://127.0.0.1:{port}"),
		};
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
			"Bearer",
			launchToken
		);
		while (true)
		{
			try
			{
				using var response = await client.GetAsync("health", ct);
				if (response.IsSuccessStatusCode)
				{
					return;
				}
			}
			catch (HttpRequestException)
			{
				// The port announcement and accept loop can become observable on adjacent
				// scheduler turns. Retry within the same bounded startup budget.
			}

			await Task.Delay(20, ct);
		}
	}

	private static async Task PumpOutputAsync(
		StreamReader output,
		TaskCompletionSource<int> port
	)
	{
		while (await output.ReadLineAsync() is { } line)
		{
			const string prefix = "MYLOMAIL_PORT=";
			if (
				line.StartsWith(prefix, StringComparison.Ordinal)
				&& int.TryParse(line[prefix.Length..], out var value)
			)
			{
				port.TrySetResult(value);
			}
		}
	}
}
