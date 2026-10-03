using System.Collections.Specialized;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Web;
using Google.Apis.Gmail.v1;
using Google.Apis.Http;
using MyloMail.Api.Domain;
using MyloMail.Api.Providers.Contracts;
using MyloMail.Api.Providers.Gmail;
using Xunit;

namespace MyloMail.Api.Tests.Providers;

/// <summary>
/// The account-wide backfill's request shape (§3): one list over the whole account, one full
/// get per message, label membership read off the fetched message. Driven through a real
/// <c>GmailService</c> over a stubbed transport, so what is asserted is what would go on the
/// wire — which is the thing that used to cost four gets for a message wearing four labels.
/// </summary>
public sealed class GmailAccountWalkTests
{
	private static readonly DateTimeOffset Now = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);

	[Fact]
	public async Task The_walk_lists_the_whole_account_without_a_label_filter_and_includes_spam_and_trash()
	{
		var server = new WalkServer().Page(null, [new("m1", [])]);

		await Walk(server, null, InitialSyncMode.Full, null, 200);

		var list = Assert.Single(server.Lists);
		Assert.Null(list["labelIds"]);
		Assert.Equal("true", list["includeSpamTrash"], ignoreCase: true);
		Assert.Equal("200", list["maxResults"]);
		Assert.Null(list["q"]);
		Assert.Null(list["pageToken"]);
	}

	/// <summary>
	/// A message in four labels is one message: fetched once, full format, carrying all four
	/// memberships for the caller to map. Per-label walking fetched it four times.
	/// </summary>
	[Fact]
	public async Task A_message_wearing_several_labels_is_fetched_once_and_carries_every_label()
	{
		var server = new WalkServer().Page(
			null,
			[
				new("m1", ["INBOX", "UNREAD", "IMPORTANT", "CATEGORY_UPDATES"]),
				new("m2", ["INBOX"]),
				new("m3", ["Label_7", "STARRED"]),
			]
		);

		var page = await Walk(server, null, InitialSyncMode.Full, null, 200);

		Assert.Equal(new[] { "m1", "m2", "m3" }, server.Fetched);
		// The SDK leaves `format` off the wire when it equals the API default, which is full.
		Assert.All(server.FetchFormats, format => Assert.True(format is null || string.Equals(format, "full", StringComparison.OrdinalIgnoreCase)));
		Assert.Equal(3, page.Messages.Count);

		var multi = page.Messages.Single(message => message.ProviderStableId == "m1");
		Assert.Equal(
			new[] { "INBOX", "UNREAD", "IMPORTANT", "CATEGORY_UPDATES" },
			multi.Occurrences.Select(o => o.ProviderMailboxId)
		);

		// Gmail's occurrence id is the message id, whatever the label (§1): the provider id
		// lives on the membership, one per label, all naming the same message.
		Assert.All(multi.Occurrences, o => Assert.Equal("m1", o.ProviderOccurrenceId));
		Assert.False(multi.IsRead);
		var starred = page.Messages.Single(message => message.ProviderStableId == "m3");
		Assert.True(starred.IsFlagged);
	}

	/// <summary>
	/// Archived mail wears no label at all. It comes back as a message with no memberships, so
	/// the caller decides what it is worth; the provider neither drops nor invents one.
	/// </summary>
	[Fact]
	public async Task A_message_with_no_labels_is_returned_with_no_memberships()
	{
		var server = new WalkServer().Page(null, [new("archived", [])]);

		var page = await Walk(server, null, InitialSyncMode.Full, null, 200);

		var message = Assert.Single(page.Messages);
		Assert.Equal("archived", message.ProviderStableId);
		Assert.Empty(message.Occurrences);
	}

	[Fact]
	public async Task A_message_listed_twice_in_a_page_is_still_fetched_once()
	{
		var server = new WalkServer().Page(
			null,
			[new("m1", ["INBOX"]), new("m1", ["INBOX"]), new("m2", ["INBOX"])]
		);

		var page = await Walk(server, null, InitialSyncMode.Full, null, 200);

		Assert.Equal(new[] { "m1", "m2" }, server.Fetched);
		Assert.Equal(2, page.Messages.Count);
	}

	/// <summary>
	/// Deleted between the list and the get is ordinary over a walk of tens of thousands of
	/// messages. Failing the page would replay a list that no longer contains it; skipping
	/// loses nothing, and the message still counts against a count bound.
	/// </summary>
	[Fact]
	public async Task A_message_deleted_after_it_was_listed_is_skipped_and_still_consumed()
	{
		var server = new WalkServer()
			.Page(null, [new("m1", ["INBOX"]), new("gone", ["INBOX"]), new("m3", ["INBOX"])], next: "p2")
			.Missing("gone");

		var page = await Walk(server, null, InitialSyncMode.LastNMessages, 10, 3);

		Assert.Equal(new[] { "m1", "m3" }, page.Messages.Select(m => m.ProviderStableId));
		var cursor = GmailMailProvider.InitialSyncCursor.Parse(
			page.ResumeToken,
			InitialSyncMode.LastNMessages,
			10
		);
		Assert.Equal(7, cursor.Remaining);
	}

	/// <summary>
	/// "Last N messages" is the newest N of the account, counted across pages of the one walk —
	/// not N per label — and the walk ends at N even though Gmail still offers another page.
	/// </summary>
	[Fact]
	public async Task A_message_bound_is_consumed_across_pages_and_ends_the_walk_at_the_bound()
	{
		var server = new WalkServer()
			.Page(null, [new("a", ["INBOX"]), new("b", ["INBOX"])], next: "p2")
			.Page("p2", [new("c", ["INBOX"]), new("d", ["INBOX"])], next: "p3")
			.Page("p3", [new("e", ["INBOX"])], next: "p4");

		var first = await Walk(server, null, InitialSyncMode.LastNMessages, 5, 2);
		Assert.True(first.HasMore);
		var second = await Walk(server, first.ResumeToken, InitialSyncMode.LastNMessages, 5, 2);
		Assert.True(second.HasMore);
		var third = await Walk(server, second.ResumeToken, InitialSyncMode.LastNMessages, 5, 2);

		Assert.False(third.HasMore);
		Assert.Null(third.ResumeToken);
		Assert.Equal(new[] { "2", "2", "1" }, server.Lists.Select(list => list["maxResults"]));
		Assert.Equal(
			new string?[] { null, "p2", "p3" },
			server.Lists.Select(list => list["pageToken"])
		);
		Assert.Equal(5, server.Fetched.Count);
		Assert.All(server.Lists, list => Assert.Null(list["labelIds"]));
	}

	[Fact]
	public async Task A_date_bound_restricts_the_whole_walk_not_each_label()
	{
		var server = new WalkServer().Page(null, [new("m1", ["INBOX"])]);

		await Walk(server, null, InitialSyncMode.LastNMonths, 3, 200);

		var list = Assert.Single(server.Lists);
		Assert.Equal($"after:{Now.AddMonths(-3).ToUnixTimeSeconds()}", list["q"]);
		Assert.Null(list["labelIds"]);
	}

	[Fact]
	public async Task Full_history_neither_limits_the_count_nor_restricts_the_date()
	{
		var server = new WalkServer().Page(null, [new("m1", ["INBOX"])], next: "p2", estimate: 9000);

		var page = await Walk(server, null, InitialSyncMode.Full, null, 200);

		Assert.True(page.HasMore);
		Assert.Equal("p2", page.ResumeToken);
		Assert.Equal(9000, page.EstimatedTotal);
		Assert.Null(Assert.Single(server.Lists)["q"]);
	}

	[Fact]
	public async Task A_count_bound_caps_the_estimated_total_at_the_bound()
	{
		var server = new WalkServer().Page(null, [new("m1", ["INBOX"])], next: "p2", estimate: 9000);

		var page = await Walk(server, null, InitialSyncMode.LastNMessages, 100, 200);

		Assert.Equal(100, page.EstimatedTotal);
	}

	private static async Task<InitialSyncPage> Walk(
		WalkServer server,
		string? resumeToken,
		InitialSyncMode mode,
		int? bound,
		int pageSize
	)
	{
		// Not disposed: the services of one test share the stub transport, and disposing a
		// service disposes the handler under it, which the test's next walk still needs.
		var service = server.Service();
		return await GmailMailProvider.AccountWalkPageAsync(
			service,
			resumeToken,
			mode,
			bound,
			pageSize,
			Now,
			CancellationToken.None
		);
	}

	private sealed record Listed(string Id, string[] Labels);

	/// <summary>A Gmail API that answers <c>messages.list</c> and <c>messages.get</c> from a script.</summary>
	private sealed class WalkServer : HttpMessageHandler
	{
		private static readonly JsonSerializerOptions Options = new()
		{
			DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
		};

		private readonly Dictionary<string, (Listed[] Messages, string? Next, long Estimate)> pages = [];
		private readonly Dictionary<string, Listed> known = [];
		private readonly HashSet<string> missing = [];

		public List<NameValueCollection> Lists { get; } = [];
		public List<string> Fetched { get; } = [];
		public List<string?> FetchFormats { get; } = [];

		public WalkServer Page(string? token, Listed[] messages, string? next = null, long estimate = 3)
		{
			pages[token ?? string.Empty] = (messages, next, estimate);
			foreach (var message in messages)
			{
				known[message.Id] = message;
			}

			return this;
		}

		public WalkServer Missing(string id)
		{
			missing.Add(id);
			return this;
		}

		public GmailService Service()
		{
			var service = new GmailService(
				new Google.Apis.Services.BaseClientService.Initializer
				{
					ApplicationName = "MyloMail tests",
					HttpClientFactory = new HttpClientFromMessageHandlerFactory(_ =>
						new HttpClientFromMessageHandlerFactory.ConfiguredHttpMessageHandler(this, false, false)
					),
				}
			);
			service.AttachThrottleTracker(new GmailThrottleTracker());
			return service;
		}

		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
		{
			var uri = request.RequestUri!;
			var query = HttpUtility.ParseQueryString(uri.Query);
			const string messages = "/gmail/v1/users/me/messages";
			if (uri.AbsolutePath == messages)
			{
				Lists.Add(query);
				var (page, next, estimate) = pages[query["pageToken"] ?? string.Empty];
				return Task.FromResult(
					Json(
						HttpStatusCode.OK,
						new
						{
							messages = page.Select(m => new { id = m.Id, threadId = $"t-{m.Id}" }),
							nextPageToken = next,
							resultSizeEstimate = estimate,
						}
					)
				);
			}

			var id = uri.AbsolutePath[(messages.Length + 1)..];
			Fetched.Add(id);
			FetchFormats.Add(query["format"]);
			if (missing.Contains(id))
			{
				return Task.FromResult(
					Json(
						HttpStatusCode.NotFound,
						new { error = new { code = 404, message = "Requested entity was not found." } }
					)
				);
			}

			return Task.FromResult(
				Json(
					HttpStatusCode.OK,
					new
					{
						id,
						threadId = $"t-{id}",
						labelIds = known[id].Labels,
						snippet = "s",
						historyId = "100",
						internalDate = "1700000000000",
						sizeEstimate = 123,
						payload = new
						{
							mimeType = "text/plain",
							headers = new[] { new { name = "Subject", value = "Hello" } },
						},
					}
				)
			);
		}

		private static HttpResponseMessage Json(HttpStatusCode status, object body) =>
			new(status)
			{
				Content = new StringContent(
					JsonSerializer.Serialize(body, Options),
					Encoding.UTF8,
					"application/json"
				),
			};
	}
}
