using System.Net;
using System.Text;
using Google.Apis.Gmail.v1;
using Google.Apis.Http;
using Microsoft.Graph;
using Microsoft.Kiota.Abstractions.Authentication;
using MyloMail.Api.Domain;
using MyloMail.Api.Providers.Contracts;
using MyloMail.Api.Providers.Gmail;
using MyloMail.Api.Providers.Graph;
using Xunit;

namespace MyloMail.Api.Tests.Providers;

/// <summary>
/// Recovery after an ambiguous attempt asks the provider about one message. What goes on the wire
/// is what matters: one read per id, never a list of the account.
/// </summary>
public sealed class MessageLookupTests
{
	private static MessageLookup Lookup(string? stableId, string? header, params string[] occurrenceIds) =>
		new(
			stableId,
			header,
			[.. occurrenceIds.Select(id => new KnownOccurrence(new Mailbox { Id = Guid.NewGuid(), ProviderMailboxId = "INBOX" }, id))],
			[]
		);

	[Fact]
	public async Task Gmail_reads_the_message_by_id_once_whatever_the_number_of_labels_and_never_lists()
	{
		var handler = new RecordingHandler(path =>
			path.EndsWith("/messages/m1", StringComparison.Ordinal)
				? (HttpStatusCode.OK, """{"id":"m1","labelIds":["INBOX","TRASH"],"internalDate":"1700000000000","payload":{"headers":[{"name":"Message-ID","value":"<a@b>"}]}}""")
				: (HttpStatusCode.NotFound, "{}")
		);
		using var service = GmailService(handler);

		// The id is known twice over (stable id and the occurrence of each label): still one read.
		var found = await GmailMailProvider.FindMessageAsync(service, Lookup("m1", "<a@b>", "m1", "m1"), default);

		var message = Assert.Single(found);
		Assert.Equal(new[] { "INBOX", "TRASH" }, message.Occurrences.Select(o => o.ProviderMailboxId));
		var request = Assert.Single(handler.Requests);
		Assert.Contains("/messages/m1", request);
		Assert.DoesNotContain("?q=", request);
	}

	[Fact]
	public async Task Gmail_says_a_deleted_message_is_gone_with_an_empty_answer()
	{
		var handler = new RecordingHandler(_ => (HttpStatusCode.NotFound, """{"error":{"code":404,"message":"Requested entity was not found."}}"""));
		using var service = GmailService(handler);

		Assert.Empty(await GmailMailProvider.FindMessageAsync(service, Lookup("m1", null), default));
	}

	[Fact]
	public async Task Gmail_finds_a_message_whose_id_was_never_recorded_by_its_message_id_header()
	{
		var handler = new RecordingHandler(path =>
			path.Contains("/messages?", StringComparison.Ordinal)
				? (HttpStatusCode.OK, """{"messages":[{"id":"m9"}]}""")
				: (HttpStatusCode.OK, """{"id":"m9","labelIds":["INBOX"],"internalDate":"1700000000000"}""")
		);
		using var service = GmailService(handler);

		var found = await GmailMailProvider.FindMessageAsync(service, Lookup(null, "<x@y>"), default);

		Assert.Equal("m9", Assert.Single(found).ProviderStableId);
		Assert.Contains("rfc822msgid", handler.Requests[0]);
	}

	[Fact]
	public async Task Graph_reads_the_message_by_immutable_id_and_reports_the_folder_it_is_in_now()
	{
		var handler = new RecordingHandler(path =>
			path.Contains("/me/messages/m1", StringComparison.Ordinal)
				? (HttpStatusCode.OK, """{"id":"m1","parentFolderId":"archive","receivedDateTime":"2026-01-01T00:00:00Z","internetMessageId":"<a@b>"}""")
				: (HttpStatusCode.NotFound, "{}")
		);
		using var client = Client(handler);

		var found = await GraphMailProvider.FindMessageAsync(client, Lookup("m1", "<a@b>", "m1"), default);

		var occurrence = Assert.Single(Assert.Single(found).Occurrences);
		Assert.Equal(("archive", "m1"), (occurrence.ProviderMailboxId, occurrence.ProviderOccurrenceId));
		Assert.All(handler.Requests, request => Assert.DoesNotContain("mailFolders", request));
	}

	[Fact]
	public async Task Graph_falls_back_to_one_search_by_internet_message_id_when_the_id_is_gone()
	{
		var handler = new RecordingHandler(path =>
			path.Contains("/me/messages/m1", StringComparison.Ordinal)
				? (HttpStatusCode.NotFound, "{}")
				: (HttpStatusCode.OK, """{"value":[{"id":"m2","parentFolderId":"inbox","receivedDateTime":"2026-01-01T00:00:00Z","internetMessageId":"<a@b>"}]}""")
		);
		using var client = Client(handler);

		var found = await GraphMailProvider.FindMessageAsync(client, Lookup("m1", "<a@b>", "m1"), default);

		Assert.Equal("m2", Assert.Single(found).ProviderStableId);
		Assert.Contains(handler.Requests, request => request.Contains("internetMessageId", StringComparison.Ordinal));
		Assert.All(handler.Requests, request => Assert.DoesNotContain("mailFolders", request));
	}

	[Fact]
	public async Task Graph_says_a_deleted_message_is_gone_only_after_both_lookups_find_nothing()
	{
		var handler = new RecordingHandler(path =>
			path.Contains("/me/messages/m1", StringComparison.Ordinal)
				? (HttpStatusCode.NotFound, "{}")
				: (HttpStatusCode.OK, """{"value":[]}"""));
		using var client = Client(handler);

		Assert.Empty(await GraphMailProvider.FindMessageAsync(client, Lookup("m1", "<a@b>", "m1"), default));
	}

	private static GmailService GmailService(HttpMessageHandler handler)
	{
		var service = new GmailService(
			new Google.Apis.Services.BaseClientService.Initializer
			{
				ApplicationName = "MyloMail tests",
				HttpClientFactory = new HttpClientFromMessageHandlerFactory(_ =>
					new HttpClientFromMessageHandlerFactory.ConfiguredHttpMessageHandler(handler, false, false)
				),
			}
		);
		service.AttachThrottleTracker(new GmailThrottleTracker());
		return service;
	}

	private static GraphServiceClient Client(HttpMessageHandler handler) =>
		new(new HttpClient(handler), new AnonymousAuthenticationProvider());

	private sealed class RecordingHandler(Func<string, (HttpStatusCode Status, string Body)> respond) : HttpMessageHandler
	{
		public List<string> Requests { get; } = [];

		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			var target = Uri.UnescapeDataString(request.RequestUri!.PathAndQuery);
			Requests.Add(target);
			var (status, body) = respond(target);
			return Task.FromResult(
				new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") }
			);
		}
	}
}
