using System.Net;
using System.Net.Http.Headers;
using Azure.Core;
using Microsoft.Graph;
using Microsoft.Kiota.Abstractions;
using MyloMail.Api.Domain;
using MyloMail.Api.Providers;
using MyloMail.Api.Providers.Graph;
using Xunit;

namespace MyloMail.Api.Tests.Providers;

public sealed class GraphUploadPipelineTests
{
	private const int ChunkSize = 320 * 1024 * 12;

	[Fact]
	public async Task Large_upload_slices_use_the_Graph_pipeline_and_exact_ranges()
	{
		var handler = new UploadHandler();
		var credential = new RecordingCredential();
		var authenticationProvider =
			GraphMailProvider.CreateAuthenticationProvider(credential);
		using var http = GraphClientFactory.Create(
			authenticationProvider,
			handlers: [new GraphImmutableIdHandler()],
			finalHandler: handler,
			disposeHandler: false
		);
		using var client = new GraphServiceClient(http, authenticationProvider);
		var attachment = new DraftAttachment
		{
			Filename = "large.bin",
			MimeType = "application/octet-stream",
			Content = new byte[ChunkSize + 7],
		};

		await GraphMailProvider.UploadLargeAttachmentContentAsync(
			client,
			"https://upload.example.test/session",
			attachment,
			CancellationToken.None
		);

		Assert.Equal(2, handler.Requests.Count);
		Assert.All(handler.Requests, request =>
		{
			Assert.Equal(HttpMethod.Put, request.Method);
			Assert.Equal("https://upload.example.test/session", request.Uri);
			Assert.Contains("IdType=\"ImmutableId\"", request.Prefer);
			Assert.Null(request.Authorization);
		});
		Assert.Equal($"bytes 0-{ChunkSize - 1}/{ChunkSize + 7}", handler.Requests[0].ContentRange);
		Assert.Equal(ChunkSize, handler.Requests[0].ContentLength);
		Assert.Equal($"bytes {ChunkSize}-{ChunkSize + 6}/{ChunkSize + 7}", handler.Requests[1].ContentRange);
		Assert.Equal(7, handler.Requests[1].ContentLength);
		Assert.Equal(0, credential.RequestCount);
	}

	[Fact]
	public async Task Upload_pipeline_translates_chunk_throttling()
	{
		var handler = new UploadHandler(throttled: true);
		var credential = new RecordingCredential();
		var authenticationProvider =
			GraphMailProvider.CreateAuthenticationProvider(credential);
		using var http = GraphClientFactory.Create(
			authenticationProvider,
			handlers: [new GraphImmutableIdHandler()],
			finalHandler: handler,
			disposeHandler: false
		);
		using var client = new GraphServiceClient(http, authenticationProvider);
		var attachment = new DraftAttachment
		{
			Filename = "large.bin",
			MimeType = "application/octet-stream",
			Content = new byte[ChunkSize + 1],
		};

		var exception = await Assert.ThrowsAsync<ProviderThrottledException>(() =>
			GraphMailProvider.UploadLargeAttachmentContentAsync(
				client,
				"https://upload.example.test/session",
				attachment,
				CancellationToken.None
			)
		);

		Assert.Equal(TimeSpan.FromSeconds(37), exception.RetryAfter);
		Assert.Single(handler.Requests);
	}

	private sealed record CapturedRequest(
		HttpMethod Method,
		string Uri,
		IReadOnlyList<string> Prefer,
		string? Authorization,
		string? ContentRange,
		long? ContentLength
	);

	private sealed class UploadHandler(bool throttled = false) : HttpMessageHandler
	{
		public List<CapturedRequest> Requests { get; } = [];

		protected override Task<HttpResponseMessage> SendAsync(
			HttpRequestMessage request,
			CancellationToken ct
		)
		{
			Requests.Add(
				new CapturedRequest(
					request.Method,
					request.RequestUri!.AbsoluteUri,
					request.Headers.GetValues("Prefer").ToList(),
					request.Headers.Authorization?.ToString(),
					request.Content?.Headers.ContentRange?.ToString(),
					request.Content?.Headers.ContentLength
				)
			);
			var response = new HttpResponseMessage(
				throttled ? HttpStatusCode.TooManyRequests : HttpStatusCode.Accepted
			);
			if (throttled)
			{
				response.Headers.RetryAfter = new RetryConditionHeaderValue(
					TimeSpan.FromSeconds(37)
				);
			}
			return Task.FromResult(response);
		}
	}
	private sealed class RecordingCredential : TokenCredential
	{
		public int RequestCount { get; private set; }

		public override AccessToken GetToken(
			TokenRequestContext requestContext,
			CancellationToken ct
		)
		{
			RequestCount++;
			return new AccessToken("test-token", DateTimeOffset.UtcNow.AddHours(1));
		}

		public override ValueTask<AccessToken> GetTokenAsync(
			TokenRequestContext requestContext,
			CancellationToken ct
		) => new(GetToken(requestContext, ct));
	}

}
