using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Graph;
using MyloMail.Api.Credentials;
using MyloMail.Api.Domain;
using MyloMail.Api.Providers;
using MyloMail.Api.Providers.Graph;
using Xunit;

namespace MyloMail.Api.Tests.Providers;

public sealed class GraphFastTransferStreamTests
{
	private static byte[] Property(int flags) => [0x03, 0x00, 0x07, 0x0E, .. BitConverter.GetBytes(flags)];

	[Theory]
	[InlineData(true, 0x0009, 0x0005)]
	[InlineData(false, 0x0009, 0x0004)]
	[InlineData(true, 0x0019, 0x0015)]
	public void A_draft_becomes_a_submitted_message_read_or_unread_as_asked(bool isRead, int draft, int expected)
	{
		byte[] stream = [0xAA, 0xBB, .. Property(draft), 0xCC];

		Assert.True(GraphFastTransferStream.MarkAsReceived(stream, isRead));

		Assert.Equal(expected, BitConverter.ToInt32(stream, 2 + 4));
		// Nothing but the value moved.
		Assert.Equal([0xAA, 0xBB, 0x03, 0x00, 0x07, 0x0E], stream[..6]);
		Assert.Equal(0xCC, stream[^1]);
	}

	[Fact]
	public void The_tag_bytes_inside_other_data_are_not_mistaken_for_the_flags()
	{
		// First a decoy with the same four bytes but a value that cannot be a draft's flags, then the real one.
		byte[] stream = [.. Property(0x7FFFFFFF), .. Property(0x0008)];

		Assert.True(GraphFastTransferStream.MarkAsReceived(stream, isRead: false));

		Assert.Equal(0x7FFFFFFF, BitConverter.ToInt32(stream, 4));
		Assert.Equal(0x0004, BitConverter.ToInt32(stream, 12));
	}

	[Fact]
	public void A_stream_without_the_flags_of_a_draft_is_left_untouched()
	{
		var withoutFlags = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
		var alreadySent = Property(0x0001);
		var copy = alreadySent.ToArray();

		Assert.False(GraphFastTransferStream.MarkAsReceived(withoutFlags, true));
		Assert.False(GraphFastTransferStream.MarkAsReceived(alreadySent, true));

		Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, withoutFlags);
		Assert.Equal(copy, alreadySent);
	}
}

/// <summary>
/// The sequence of calls that stores a received message in a Microsoft 365 mailbox, against a scripted
/// Graph: the order matters (the draft goes before the import, the import is last), and a failure before
/// the import must leave no draft behind and import nothing.
/// </summary>
public sealed class GraphImportTests
{
	private static readonly byte[] DraftStream = [0x10, 0x20, 0x03, 0x00, 0x07, 0x0E, 0x09, 0x00, 0x00, 0x00, 0x30];

	private sealed class Script : HttpMessageHandler
	{
		public List<string> Calls { get; } = [];
		public List<string> Bodies { get; } = [];
		public byte[] Exported { get; set; } = DraftStream;
		public HttpStatusCode ExportStatus { get; set; } = HttpStatusCode.OK;

		protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
		{
			var path = request.RequestUri!.AbsolutePath;
			Calls.Add($"{request.Method} {path}");
			Bodies.Add(request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(ct));
			return (request.Method.Method, path) switch
			{
				("GET", "/beta/me/settings/exchange") => Json("{\"primaryMailboxId\":\"MBX:abc@def\"}"),
				("POST", "/v1.0/me/translateExchangeIds") => Json("{\"value\":[{\"sourceId\":\"imm\",\"targetId\":\"rest-folder\"}]}"),
				("POST", "/v1.0/me/messages") => Json("{\"id\":\"draft-1\"}"),
				("POST", "/beta/admin/exchange/mailboxes/MBX%3Aabc%40def/exportItems") when ExportStatus != HttpStatusCode.OK =>
					new HttpResponseMessage(ExportStatus),
				("POST", "/beta/admin/exchange/mailboxes/MBX%3Aabc%40def/exportItems") =>
					Json($"{{\"value\":[{{\"itemId\":\"draft-1\",\"data\":\"{Convert.ToBase64String(Exported)}\"}}]}}"),
				("POST", "/v1.0/me/messages/draft-1/permanentDelete") => new HttpResponseMessage(HttpStatusCode.NoContent),
				("POST", "/beta/admin/exchange/mailboxes/MBX%3Aabc%40def/createImportSession") =>
					Json("{\"importUrl\":\"https://outlook.example.test/import?token=1\"}"),
				("PATCH", "/v1.0/me/messages/imported-1") => Json("{}"),
				_ => new HttpResponseMessage(HttpStatusCode.NotFound),
			};
		}

		private static HttpResponseMessage Json(string body) =>
			new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
	}

	private sealed class ImportScript(HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
	{
		public List<string> Bodies { get; } = [];

		protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
		{
			Bodies.Add(await request.Content!.ReadAsStringAsync(ct));
			var response = new HttpResponseMessage(status)
			{
				Content = new StringContent("{\"itemId\":\"imported-1\"}", Encoding.UTF8, "application/json"),
			};
			if (status == HttpStatusCode.TooManyRequests)
			{
				response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(90));
			}

			return response;
		}
	}

	private static (GraphMailProvider Provider, Account Account, MyloMail.Api.Domain.Mailbox Folder) Build(Script graph, ImportScript import)
	{
		var provider = new GraphMailProvider(
			new GraphOAuthAuthenticator(new InMemoryCredentialStore(), "client-id", "https://login.microsoftonline.com/common")
		)
		{
			ImportClientFactory = (_, _) => Task.FromResult(new GraphServiceClient(new HttpClient(graph, disposeHandler: false))),
			ImportHandler = import,
		};
		return (
			provider,
			new Account { Id = Guid.NewGuid(), ProviderType = ProviderType.Microsoft365, DisplayName = "Work" },
			new MyloMail.Api.Domain.Mailbox { Id = Guid.NewGuid(), ProviderMailboxId = "imm", Name = "Inbox" }
		);
	}

	private static readonly byte[] Mime = Encoding.ASCII.GetBytes("From: a@example.test\r\nMessage-ID: <1@example.test>\r\n\r\nBody\r\n");

	[Fact]
	public async Task A_message_is_created_exported_unflagged_as_a_draft_deleted_then_imported_last()
	{
		var graph = new Script();
		var import = new ImportScript();
		var (provider, account, folder) = Build(graph, import);

		await provider.ImportMessageAsync(account, folder, Mime, isRead: false, isFlagged: true, CancellationToken.None);

		Assert.Equal(
			[
				"GET /beta/me/settings/exchange",
				"POST /v1.0/me/translateExchangeIds",
				"POST /v1.0/me/messages",
				"POST /beta/admin/exchange/mailboxes/MBX%3Aabc%40def/exportItems",
				"POST /v1.0/me/messages/draft-1/permanentDelete",
				"POST /beta/admin/exchange/mailboxes/MBX%3Aabc%40def/createImportSession",
				"PATCH /v1.0/me/messages/imported-1",
			],
			graph.Calls
		);
		// The MIME went up exactly as received, base64 encoded, as the create call requires.
		Assert.Equal(Convert.ToBase64String(Mime), graph.Bodies[2]);

		var imported = JsonDocument.Parse(Assert.Single(import.Bodies)).RootElement;
		Assert.Equal("rest-folder", imported.GetProperty("FolderId").GetString());
		Assert.Equal("create", imported.GetProperty("Mode").GetString());
		var data = Convert.FromBase64String(imported.GetProperty("Data").GetString()!);
		// Only the flags changed: no longer unsent, submitted, unread (0x9 -> 0x4).
		Assert.Equal(0x0004, BitConverter.ToInt32(data, 6));
		Assert.Equal(DraftStream[..6], data[..6]);
		Assert.Equal(DraftStream[10], data[10]);
		Assert.Contains("Flagged", graph.Bodies[^1], StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public async Task An_export_that_is_not_recognised_deletes_the_draft_and_imports_nothing()
	{
		var graph = new Script { Exported = [1, 2, 3, 4, 5, 6, 7, 8] };
		var import = new ImportScript();
		var (provider, account, folder) = Build(graph, import);

		await Assert.ThrowsAsync<NotSupportedException>(() =>
			provider.ImportMessageAsync(account, folder, Mime, true, false, CancellationToken.None));

		Assert.Contains("POST /v1.0/me/messages/draft-1/permanentDelete", graph.Calls);
		Assert.Empty(import.Bodies);
	}

	[Fact]
	public async Task A_failed_export_leaves_no_draft_and_imports_nothing()
	{
		var graph = new Script { ExportStatus = HttpStatusCode.InternalServerError };
		var import = new ImportScript();
		var (provider, account, folder) = Build(graph, import);

		await Assert.ThrowsAnyAsync<Exception>(() =>
			provider.ImportMessageAsync(account, folder, Mime, true, false, CancellationToken.None));

		Assert.Contains("POST /v1.0/me/messages/draft-1/permanentDelete", graph.Calls);
		Assert.Empty(import.Bodies);
	}

	[Fact]
	public async Task A_throttled_import_is_reported_as_throttling_with_the_wait_asked_for()
	{
		var (provider, account, folder) = Build(new Script(), new ImportScript(HttpStatusCode.TooManyRequests));

		var thrown = await Assert.ThrowsAsync<ProviderThrottledException>(() =>
			provider.ImportMessageAsync(account, folder, Mime, true, false, CancellationToken.None));

		Assert.Equal(TimeSpan.FromSeconds(90), thrown.RetryAfter);
	}

	[Fact]
	public async Task A_refused_import_is_not_an_authentication_failure()
	{
		var (provider, account, folder) = Build(new Script(), new ImportScript(HttpStatusCode.Forbidden));

		await Assert.ThrowsAsync<NotSupportedException>(() =>
			provider.ImportMessageAsync(account, folder, Mime, true, false, CancellationToken.None));
	}
}
