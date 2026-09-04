using MyloMail.Api.Providers;
using Xunit;

namespace MyloMail.Api.Tests.Providers;

/// <summary>
/// <see cref="CalendarProviderFactory.CertificateRejectionHandler"/> is held and reused across
/// every request a <c>CalDavCalendarProvider</c> makes over its whole lifetime, not recreated
/// per call — so a rejection recorded on one request must not survive to mislabel a later,
/// unrelated transport failure on the same handler instance. Hundredth architecture-review
/// pass; invariant-review caught this exact staleness bug in the fix's first version.
/// </summary>
public sealed class CertificateRejectionHandlerTests
{
	[Fact]
	public async Task A_stale_rejection_from_an_earlier_request_does_not_taint_a_later_unrelated_failure()
	{
		CalendarProviderFactory.CertificateRejectionHandler? handler = null;
		var inner = new ScriptedHandler(
			// First request: stands in for the real validation callback setting Rejected
			// during the handshake, moments before it surfaces as this exception.
			request =>
			{
				handler!.Rejected = ("mail.example.test", "aa".PadRight(64, 'a'), "CN=Example");
				throw new HttpRequestException("SSL connection could not be established.");
			},
			// Second request: an unrelated transport failure — nothing to do with certificates.
			// Nothing sets Rejected for it.
			request =>
			{
				throw new HttpRequestException("Connection refused.");
			}
		);
		handler = new CalendarProviderFactory.CertificateRejectionHandler(inner);
		var invoker = new HttpMessageInvoker(handler);

		var first = await Assert.ThrowsAsync<ProviderAuthenticationException>(
			() => invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://mail.example.test/"), default)
		);
		Assert.Contains("mail.example.test", first.Message);

		// The second request's own validation callback never rejects anything, so nothing sets
		// Rejected again — if the handler failed to reset it after the first request, this
		// would still see the first request's stale tuple and wrongly report the second,
		// unrelated failure as the same certificate problem.
		var second = await Assert.ThrowsAsync<HttpRequestException>(
			() => invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://mail.example.test/"), default)
		);
		Assert.Equal("Connection refused.", second.Message);
	}

	private sealed class ScriptedHandler(
		params Func<HttpRequestMessage, HttpResponseMessage>[] responses
	) : HttpMessageHandler
	{
		private int index;

		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
			Task.FromResult(responses[index++](request));
	}
}
