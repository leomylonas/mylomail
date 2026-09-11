using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Google.Apis.Gmail.v1;
using Google.Apis.Http;
using Microsoft.Graph;
using Microsoft.Kiota.Abstractions.Authentication;
using MyloMail.Api.Providers;
using MyloMail.Api.Providers.Contracts;
using MyloMail.Api.Providers.Gmail;
using MyloMail.Api.Providers.Graph;
using Xunit;

namespace MyloMail.Api.Tests.Providers;

public sealed class ProviderMutationBatchingTests
{
	[Fact]
	public async Task Gmail_trashes_multiple_messages_in_one_HTTP_batch()
	{
		var handler = new GmailBatchHandler();
		using var service = CreateGmailService(handler);
		var refs = References();

		var result = await GmailMailProvider.MoveToTrashBatchAsync(
			service,
			refs,
			CancellationToken.None
		);

		Assert.Equal(1, handler.RequestCount);
		Assert.Equal(2, result.Items.Count);
		Assert.True(result.Items.Single(item => item.MessageId == refs[0].MessageId).Succeeded);
		Assert.Equal(
			"NOTFOUND",
			result.Items.Single(item => item.MessageId == refs[1].MessageId).Problem?.ProviderCode
		);
	}

	[Fact]
	public async Task Gmail_batch_uses_the_longest_item_retry_after()
	{
		var handler = new GmailBatchHandler(GmailBatchMode.ItemsThrottled);
		using var service = CreateGmailService(handler);

		var exception = await Assert.ThrowsAsync<ProviderThrottledException>(() =>
			GmailMailProvider.MoveToTrashBatchAsync(
				service,
				References(),
				CancellationToken.None
			)
		);

		Assert.Equal(TimeSpan.FromSeconds(60), exception.RetryAfter);
		Assert.Equal(1, handler.RequestCount);
	}

	[Fact]
	public async Task Gmail_outer_batch_throttle_is_translated()
	{
		var handler = new GmailBatchHandler(GmailBatchMode.OuterThrottled);
		using var service = CreateGmailService(handler);

		var exception = await Assert.ThrowsAsync<ProviderThrottledException>(() =>
			GmailMailProvider.MoveToTrashBatchAsync(
				service,
				References(),
				CancellationToken.None
			)
		);

		Assert.Equal(TimeSpan.FromSeconds(45), exception.RetryAfter);
		Assert.Equal(1, handler.RequestCount);
	}

	[Fact]
	public async Task Graph_permanently_deletes_multiple_messages_in_one_HTTP_batch()
	{
		var handler = new GraphBatchHandler();
		using var http = new HttpClient(handler);
		using var client = new GraphServiceClient(http, new AnonymousAuthenticationProvider());
		var refs = References();

		var result = await GraphMailProvider.DeletePermanentlyBatchAsync(
			client,
			refs,
			CancellationToken.None
		);

		Assert.Equal(1, handler.RequestCount);
		Assert.Equal(2, result.Items.Count);
		Assert.True(result.Items.Single(item => item.MessageId == refs[0].MessageId).Succeeded);
		Assert.Equal(
			"NOTFOUND",
			result.Items.Single(item => item.MessageId == refs[1].MessageId).Problem?.ProviderCode
		);
	}

	[Fact]
	public async Task Graph_batch_uses_the_longest_item_retry_after()
	{
		var handler = new GraphBatchHandler(throttled: true);
		using var http = new HttpClient(handler);
		using var client = new GraphServiceClient(
			http,
			new AnonymousAuthenticationProvider()
		);

		var exception = await Assert.ThrowsAsync<ProviderThrottledException>(() =>
			GraphMailProvider.DeletePermanentlyBatchAsync(
				client,
				References(),
				CancellationToken.None
			)
		);

		Assert.Equal(TimeSpan.FromSeconds(60), exception.RetryAfter);
		Assert.Equal(1, handler.RequestCount);
	}

	private static MessageOccurrenceRef[] References() =>
	[
		new(Guid.NewGuid(), Guid.NewGuid(), "message-one"),
		new(Guid.NewGuid(), Guid.NewGuid(), "message-two"),
	];

	private static GmailService CreateGmailService(HttpMessageHandler handler)
	{
		var service = new GmailService(
			new Google.Apis.Services.BaseClientService.Initializer
			{
				ApplicationName = "MyloMail tests",
				HttpClientFactory = new HttpClientFromMessageHandlerFactory(_ =>
					new HttpClientFromMessageHandlerFactory.ConfiguredHttpMessageHandler(
						handler,
						false,
						false
					)
				),
			}
		);
		service.AttachThrottleTracker(new GmailThrottleTracker());
		return service;
	}

	private enum GmailBatchMode
	{
		Results,
		ItemsThrottled,
		OuterThrottled,
	}

	private sealed class GmailBatchHandler(
		GmailBatchMode mode = GmailBatchMode.Results
	) : HttpMessageHandler
	{
		public int RequestCount { get; private set; }

		protected override async Task<HttpResponseMessage> SendAsync(
			HttpRequestMessage request,
			CancellationToken ct
		)
		{
			RequestCount++;
			Assert.Equal(HttpMethod.Post, request.Method);
			var payload = await request.Content!.ReadAsStringAsync(ct);
			Assert.Contains("message-one/trash", payload);
			Assert.Contains("message-two/trash", payload);

			if (mode == GmailBatchMode.OuterThrottled)
			{
				var throttled = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
				{
					Content = new StringContent(
						"{\"error\":{\"code\":429,\"message\":\"throttled\"}}",
						Encoding.UTF8,
						"application/json"
					),
				};
				throttled.Headers.RetryAfter = new RetryConditionHeaderValue(
					TimeSpan.FromSeconds(45)
				);
				return throttled;
			}

			const string boundary = "batch_response";
			var body = mode == GmailBatchMode.ItemsThrottled
				? ThrottledBody(boundary)
				: ResultsBody(boundary);
			var response = new HttpResponseMessage(HttpStatusCode.OK)
			{
				Content = new StringContent(body, Encoding.UTF8),
			};
			response.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(
				$"multipart/mixed; boundary={boundary}"
			);
			return response;
		}

		private static string ResultsBody(string boundary) =>
			string.Join(
				"\r\n",
				$"--{boundary}",
				"Content-Type: application/http",
				"Content-ID: response-1",
				"",
				"HTTP/1.1 200 OK",
				"Content-Type: application/json",
				"",
				"{\"id\":\"message-one\"}",
				$"--{boundary}",
				"Content-Type: application/http",
				"Content-ID: response-2",
				"",
				"HTTP/1.1 404 Not Found",
				"Content-Type: application/json",
				"",
				"{\"error\":{\"code\":404,\"message\":\"not found\"}}",
				$"--{boundary}--",
				""
			);

		private static string ThrottledBody(string boundary) =>
			string.Join(
				"\r\n",
				$"--{boundary}",
				"Content-Type: application/http",
				"Content-ID: response-1",
				"",
				"HTTP/1.1 429 Too Many Requests",
				"Content-Type: application/json",
				"Retry-After: 5",
				"",
				"{\"error\":{\"code\":429,\"message\":\"throttled\"}}",
				$"--{boundary}",
				"Content-Type: application/http",
				"Content-ID: response-2",
				"",
				"HTTP/1.1 429 Too Many Requests",
				"Content-Type: application/json",
				"Retry-After: 60",
				"",
				"{\"error\":{\"code\":429,\"message\":\"throttled\"}}",
				$"--{boundary}--",
				""
			);
	}

	private sealed class GraphBatchHandler(bool throttled = false) : HttpMessageHandler
	{
		public int RequestCount { get; private set; }

		protected override async Task<HttpResponseMessage> SendAsync(
			HttpRequestMessage request,
			CancellationToken ct
		)
		{
			RequestCount++;
			Assert.Equal(HttpMethod.Post, request.Method);
			Assert.EndsWith("/$batch", request.RequestUri?.AbsolutePath);
			using var payload = JsonDocument.Parse(await request.Content!.ReadAsStreamAsync(ct));
			var requests = payload.RootElement.GetProperty("requests").EnumerateArray().ToList();
			Assert.Equal(2, requests.Count);
			Assert.All(
				requests,
				entry =>
					Assert.Equal(
						"IdType=\"ImmutableId\"",
						entry
							.GetProperty("headers")
							.EnumerateObject()
							.Single(header =>
								header.Name.Equals("Prefer", StringComparison.OrdinalIgnoreCase)
							)
							.Value.GetString()
					)
			);

			var body = JsonSerializer.Serialize(
				new
				{
					responses = requests.Select(
						(entry, index) => new
						{
							id = entry.GetProperty("id").GetString(),
							status = throttled ? 429 : index == 0 ? 204 : 404,
							headers = throttled
								? new Dictionary<string, string>
								{
									["Retry-After"] = index == 0 ? "5" : "60",
								}
								: [],
						}
					),
				}
			);
			return new HttpResponseMessage(HttpStatusCode.OK)
			{
				Content = new StringContent(body, Encoding.UTF8, "application/json"),
			};
		}
	}
}
