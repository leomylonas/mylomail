using System.Diagnostics;
using System.Net.Http.Headers;
using Microsoft.Extensions.DependencyInjection;
using MimeKit;
using MyloMail.Api.Domain;
using MyloMail.Api.FaultInjection;
using MyloMail.Api.Persistence;
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
			CreateNoWindow = true,
		};
		start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "MyloMail.Api.dll"));
		start.Environment["DOTNET_ENVIRONMENT"] = "FaultInjection";
		start.Environment["ASPNETCORE_ENVIRONMENT"] = "FaultInjection";
		start.Environment["MYLOMAIL_LAUNCH_TOKEN"] = launchToken;
		start.Environment["MYLOMAIL_MASTER_PASSWORD"] = "process-fault-password";
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
		var port = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
		var standardOutput = PumpOutputAsync(process.StandardOutput, port);
		var standardError = process.StandardError.ReadToEndAsync();
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
		var exited = process.WaitForExitAsync(timeout.Token);
		var completed = await Task.WhenAny(port.Task, exited);
		if (completed == exited)
		{
			await standardOutput;
			throw new InvalidOperationException(
				$"Backend exited before reporting a port: {await standardError}"
			);
		}
		var boundPort = await port.Task.WaitAsync(timeout.Token);
		await WaitForHealthAsync(boundPort, launchToken, timeout.Token);
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
