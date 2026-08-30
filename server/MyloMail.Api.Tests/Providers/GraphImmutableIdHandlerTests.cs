using MyloMail.Api.Providers.Graph;
using Xunit;

namespace MyloMail.Api.Tests.Providers;

public sealed class GraphImmutableIdHandlerTests
{
	[Fact]
	public async Task Adds_immutable_id_preference_to_every_outbound_request()
	{
		var captured = new List<HttpRequestMessage>();
		using var client = new HttpClient(
			new GraphImmutableIdHandler { InnerHandler = new CapturingHandler(captured) }
		);

		await client.GetAsync("https://graph.microsoft.com/v1.0/me/mailFolders");
		await client.PostAsync("https://graph.microsoft.com/v1.0/me/messages/id/move", null);

		Assert.Equal(2, captured.Count);
		Assert.All(
			captured,
			request =>
				Assert.Contains("IdType=\"ImmutableId\"", request.Headers.GetValues("Prefer"))
		);
	}

	private sealed class CapturingHandler(List<HttpRequestMessage> captured) : HttpMessageHandler
	{
		protected override Task<HttpResponseMessage> SendAsync(
			HttpRequestMessage request,
			CancellationToken ct
		)
		{
			captured.Add(request);
			return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
		}
	}
}
