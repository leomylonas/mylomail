using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Google.Apis.Gmail.v1;
using Google.Apis.Http;
using MyloMail.Api.Errors;
using MyloMail.Api.Providers.Contracts;
using MyloMail.Api.Providers.Gmail;
using Xunit;

namespace MyloMail.Api.Tests.Providers;

/// <summary>
/// Google refuses <c>batchDelete</c> unless the app holds the full-access scope. That refusal is
/// definite, so it must fail the items once with an explanation — not escape as an exception that
/// recovery treats as an outcome in doubt and repeats every few seconds.
/// </summary>
public sealed class GmailPermanentDeleteScopeTests
{
	[Fact]
	public async Task A_missing_full_access_scope_fails_each_item_with_a_reason_instead_of_throwing()
	{
		var handler = new Handler();
		using var service = Service(handler);
		MessageOccurrenceRef[] refs =
		[
			new(Guid.NewGuid(), Guid.NewGuid(), "message-one"),
			new(Guid.NewGuid(), Guid.NewGuid(), "message-two"),
		];

		var result = await GmailMailProvider.DeletePermanentlyBatchAsync(service, refs, default);

		Assert.Equal(1, handler.DeleteCalls);
		Assert.Equal(2, result.Items.Count);
		Assert.All(result.Items, item =>
		{
			Assert.False(item.Succeeded);
			Assert.Equal("INSUFFICIENT_SCOPE", item.Problem?.ProviderCode);
			Assert.Equal(ErrorCategory.ProviderRejected, item.Problem?.Category);
			Assert.Empty(item.OccurrenceChanges);
		});
	}

	[Fact]
	public async Task An_unanswered_consent_screen_ends_in_a_timeout_instead_of_waiting_for_ever()
	{
		await Assert.ThrowsAsync<TimeoutException>(() =>
			GmailMailProvider.WithConsentTimeoutAsync<int>(
				async token =>
				{
					await Task.Delay(Timeout.Infinite, token);
					return 0;
				},
				TimeSpan.FromMilliseconds(50),
				default
			)
		);
	}

	[Fact]
	public async Task A_cancelled_job_still_cancels_rather_than_reporting_a_timeout()
	{
		using var cancelled = new CancellationTokenSource();
		await cancelled.CancelAsync();

		await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
			GmailMailProvider.WithConsentTimeoutAsync<int>(
				async token =>
				{
					await Task.Delay(Timeout.Infinite, token);
					return 0;
				},
				TimeSpan.FromMinutes(5),
				cancelled.Token
			)
		);
	}

	private static GmailService Service(HttpMessageHandler handler)
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

	private sealed class Handler : HttpMessageHandler
	{
		public int DeleteCalls { get; private set; }

		protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
		{
			if (request.RequestUri!.AbsolutePath.EndsWith("batchDelete", StringComparison.Ordinal))
			{
				DeleteCalls++;
				return new HttpResponseMessage(HttpStatusCode.Forbidden)
				{
					Content = new StringContent(
						"""{"error":{"code":403,"message":"Insufficient Permission","errors":[{"message":"Insufficient Permission","domain":"global","reason":"insufficientPermissions"}],"status":"PERMISSION_DENIED"}}""",
						Encoding.UTF8,
						"application/json"
					),
				};
			}

			// The existence preflight: both messages are there.
			_ = await request.Content!.ReadAsStringAsync(ct);
			const string boundary = "batch_response";
			string Part(int n, string id) =>
				string.Join("\r\n", $"--{boundary}", "Content-Type: application/http", $"Content-ID: response-{n}", "", "HTTP/1.1 200 OK", "Content-Type: application/json", "", $"{{\"id\":\"{id}\"}}");
			var body = string.Join("\r\n", Part(1, "message-one"), Part(2, "message-two"), $"--{boundary}--", "");
			var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8) };
			response.Content.Headers.ContentType = MediaTypeHeaderValue.Parse($"multipart/mixed; boundary={boundary}");
			return response;
		}
	}
}
