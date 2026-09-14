using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Graph;
using Microsoft.Kiota.Abstractions.Authentication;
using MyloMail.Api.Domain;
using MyloMail.Api.Errors;
using MyloMail.Api.Providers;
using MyloMail.Api.Providers.Graph;
using Xunit;

namespace MyloMail.Api.Tests.Providers;

public sealed class GraphDraftPersistenceTests
{
	[Fact]
	public void Draft_message_carries_the_selected_from_identity_and_inline_attachment()
	{
		var message = GraphMailProvider.ToDraftMessage(new Draft
		{
			FromAddress = "alias@example.test",
			Attachments =
			[
				new DraftAttachment
				{
					Filename = "logo.png",
					MimeType = "image/png",
					ContentId = "logo@example.test",
					IsInline = true,
					Content = [1, 2, 3],
				},
			],
		});

		Assert.Equal("alias@example.test", message.From?.EmailAddress?.Address);
		var attachment = Assert.IsType<Microsoft.Graph.Models.FileAttachment>(Assert.Single(message.Attachments!));
		Assert.Equal("logo.png", attachment.Name);
		Assert.True(attachment.IsInline);
		Assert.Equal("logo@example.test", attachment.ContentId);
		Assert.Equal([1, 2, 3], attachment.ContentBytes);
	}

	[Fact]
	public async Task Draft_attachments_use_inline_and_upload_session_transport_paths()
	{
		var handler = new DraftAttachmentHandler();
		using var http = new HttpClient(handler);
		using var client = new GraphServiceClient(http, new AnonymousAuthenticationProvider());
		var large = new byte[3 * 1024 * 1024 + 1];

		await GraphMailProvider.UploadDraftAttachmentsAsync(
			client,
			"draft-id",
			[
				new DraftAttachment
				{
					Filename = "small.txt",
					MimeType = "text/plain",
					Content = Encoding.UTF8.GetBytes("small attachment"),
				},
				new DraftAttachment
				{
					Filename = "large.bin",
					MimeType = "application/octet-stream",
					Content = large,
				},
			],
			CancellationToken.None
		);

		Assert.Equal("small.txt", handler.InlineAttachmentName);
		Assert.Equal("text/plain", handler.InlineAttachmentContentType);
		Assert.Equal(1, handler.UploadSessionRequests);
		Assert.Equal(1, handler.UploadRequests);
		Assert.Equal("large.bin", handler.UploadAttachmentName);
		Assert.Equal("application/octet-stream", handler.UploadAttachmentContentType);
		Assert.Equal(large.LongLength, handler.UploadAttachmentSize);
	}

	[Fact]
	public async Task Draft_attachment_update_replaces_the_remote_attachment_set()
	{
		var handler = new DraftReplacementHandler();
		using var http = new HttpClient(handler);
		using var client = new GraphServiceClient(http, new AnonymousAuthenticationProvider());

		await GraphMailProvider.ReplaceDraftAttachmentsAsync(
			client,
			"draft-id",
			[
				new DraftAttachment
				{
					Filename = "replacement.txt",
					MimeType = "text/plain",
					Content = Encoding.UTF8.GetBytes("replacement"),
				},
			],
			CancellationToken.None
		);

		Assert.Equal(1, handler.DeleteRequests);
		Assert.Equal("replacement.txt", handler.ReplacementName);
	}

	[Fact]
	public async Task Draft_from_identity_that_Graph_does_not_preserve_is_rejected()
	{
		using var http = new HttpClient(new FromIdentityHandler("primary@example.test"));
		using var client = new GraphServiceClient(http, new AnonymousAuthenticationProvider());

		var exception = await Assert.ThrowsAsync<ProviderDraftRejectedException>(() =>
			GraphMailProvider.EnsureDraftFromIdentityAsync(
				client,
				"draft-id",
				"alias@example.test",
				CancellationToken.None
			)
		);

		Assert.Equal("draft-id", exception.Draft.ProviderDraftId);
		Assert.Equal("W/\"draft-revision\"", exception.Draft.ProviderRevision);
		Assert.Contains("alias@example.test", exception.Message);
		Assert.Equal(
			ErrorCategory.ProviderRejected,
			MutationProblemTransport.FromProviderException(exception).Category
		);
	}

	[Fact]
	public async Task Partial_attachment_update_surfaces_the_post_patch_revision_as_a_conflict()
	{
		using var http = new HttpClient(new PartialDraftUpdateHandler());
		using var client = new GraphServiceClient(http, new AnonymousAuthenticationProvider());

		var exception = await Assert.ThrowsAsync<ProviderDraftRejectedException>(() =>
			GraphMailProvider.CreateOrUpdateDraftWithClientAsync(
				client,
				new Draft
				{
					ProviderDraftId = "draft-id",
					Attachments =
					[
						new DraftAttachment
						{
							Filename = "replacement.txt",
							MimeType = "text/plain",
							Content = Encoding.UTF8.GetBytes("replacement"),
						},
					],
				},
				"W/\"stale-revision\"",
				CancellationToken.None
			)
		);

		Assert.Equal("draft-id", exception.Draft.ProviderDraftId);
		Assert.Equal("W/\"post-patch-revision\"", exception.Draft.ProviderRevision);
	}

	[Fact]
	public async Task Initial_create_followup_failure_preserves_the_created_Graph_identity()
	{
		using var http = new HttpClient(new PartialDraftCreateHandler());
		using var client = new GraphServiceClient(http, new AnonymousAuthenticationProvider());

		var exception = await Assert.ThrowsAsync<ProviderDraftRejectedException>(() =>
			GraphMailProvider.CreateOrUpdateDraftWithClientAsync(
				client,
				new Draft { FromAddress = "alias@example.test" },
				expectedRevision: null,
				CancellationToken.None
			)
		);

		Assert.Equal("created-draft", exception.Draft.ProviderDraftId);
		Assert.Equal("W/\"created-revision\"", exception.Draft.ProviderRevision);
		Assert.NotNull(exception.InnerException);
	}

	private sealed class PartialDraftCreateHandler : HttpMessageHandler
	{
		protected override Task<HttpResponseMessage> SendAsync(
			HttpRequestMessage request,
			CancellationToken ct
		)
		{
			var path = request.RequestUri!.AbsolutePath;
			if (request.Method == HttpMethod.Post && path.EndsWith("/messages", StringComparison.Ordinal))
			{
				return Task.FromResult(Json(
					"""{"id":"created-draft","@odata.etag":"W/\"created-revision\""}"""
				));
			}
			if (
				request.Method == HttpMethod.Get
				&& path.EndsWith("/messages/created-draft", StringComparison.Ordinal)
			)
			{
				return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
			}

			throw new Xunit.Sdk.XunitException(
				$"Unexpected Graph request: {request.Method} {request.RequestUri}"
			);
		}
	}

	private sealed class PartialDraftUpdateHandler : HttpMessageHandler
	{
		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
		{
			var path = request.RequestUri!.AbsolutePath;
			if (request.Method == HttpMethod.Patch && path.EndsWith("/messages/draft-id", StringComparison.Ordinal))
			{
				Assert.Contains("W/\"stale-revision\"", request.Headers.GetValues("If-Match"));
				return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
			}
			if (request.Method == HttpMethod.Get && path.EndsWith("/messages/draft-id/attachments", StringComparison.Ordinal))
			{
				return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
			}
			if (request.Method == HttpMethod.Get && path.EndsWith("/messages/draft-id", StringComparison.Ordinal))
			{
				return Task.FromResult(Json("""{"id":"draft-id","@odata.etag":"W/\"post-patch-revision\""}"""));
			}

			throw new Xunit.Sdk.XunitException($"Unexpected Graph request: {request.Method} {request.RequestUri}");
		}
	}

	private sealed class DraftAttachmentHandler : HttpMessageHandler
	{
		public string? InlineAttachmentName { get; private set; }
		public string? InlineAttachmentContentType { get; private set; }
		public int UploadSessionRequests { get; private set; }
		public string? UploadAttachmentName { get; private set; }
		public string? UploadAttachmentContentType { get; private set; }
		public long? UploadAttachmentSize { get; private set; }
		public int UploadRequests { get; private set; }

		protected override async Task<HttpResponseMessage> SendAsync(
			HttpRequestMessage request,
			CancellationToken ct
		)
		{
			var path = request.RequestUri!.AbsolutePath;
			if (request.Method == HttpMethod.Post && path.EndsWith("/attachments", StringComparison.Ordinal))
			{
				using var body = JsonDocument.Parse(await request.Content!.ReadAsStreamAsync(ct));
				InlineAttachmentName = body.RootElement.GetProperty("name").GetString();
				InlineAttachmentContentType = body.RootElement.GetProperty("contentType").GetString();
				return Json("{\"id\":\"small-id\"}");
			}
			if (request.Method == HttpMethod.Post && path.EndsWith("/createUploadSession", StringComparison.Ordinal))
			{
				using var body = JsonDocument.Parse(await request.Content!.ReadAsStreamAsync(ct));
				var item = body.RootElement
					.EnumerateObject()
					.Single(property =>
						string.Equals(
							property.Name,
							"attachmentItem",
							StringComparison.OrdinalIgnoreCase
						)
					)
					.Value;
				UploadAttachmentName = item.GetProperty("name").GetString();
				UploadAttachmentContentType = item.GetProperty("contentType").GetString();
				UploadAttachmentSize = item.GetProperty("size").GetInt64();
				UploadSessionRequests++;
				return Json("{\"uploadUrl\":\"https://upload.example.test/session\"}");
			}
			if (request.Method == HttpMethod.Put && request.RequestUri!.Host == "upload.example.test")
			{
				UploadRequests++;
				return new HttpResponseMessage(HttpStatusCode.Accepted);
			}

			throw new Xunit.Sdk.XunitException($"Unexpected Graph request: {request.Method} {request.RequestUri}");
		}
	}

	private sealed class DraftReplacementHandler : HttpMessageHandler
	{
		public int DeleteRequests { get; private set; }
		public string? ReplacementName { get; private set; }

		protected override async Task<HttpResponseMessage> SendAsync(
			HttpRequestMessage request,
			CancellationToken ct
		)
		{
			var path = request.RequestUri!.AbsolutePath;
			if (request.Method == HttpMethod.Get && path.EndsWith("/attachments", StringComparison.Ordinal))
			{
				return Json("{\"value\":[{\"id\":\"old-attachment\"}]}");
			}
			if (request.Method == HttpMethod.Delete && path.EndsWith("/attachments/old-attachment", StringComparison.Ordinal))
			{
				DeleteRequests++;
				return new HttpResponseMessage(HttpStatusCode.NoContent);
			}
			if (request.Method == HttpMethod.Post && path.EndsWith("/attachments", StringComparison.Ordinal))
			{
				using var body = JsonDocument.Parse(await request.Content!.ReadAsStreamAsync(ct));
				ReplacementName = body.RootElement.GetProperty("name").GetString();
				return Json("{\"id\":\"replacement\"}");
			}

			throw new Xunit.Sdk.XunitException($"Unexpected Graph request: {request.Method} {request.RequestUri}");
		}
	}

	private sealed class FromIdentityHandler(string fromAddress) : HttpMessageHandler
	{
		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
			Task.FromResult(
				Json(
					$"{{\"id\":\"draft-id\",\"@odata.etag\":\"W/\\\"draft-revision\\\"\",\"from\":{{\"emailAddress\":{{\"address\":\"{fromAddress}\"}}}}}}"
				)
			);
	}

	private static HttpResponseMessage Json(string content) =>
		new(HttpStatusCode.OK) { Content = new StringContent(content, Encoding.UTF8, "application/json") };
}
