using System.Net;
using System.Text;
using Azure.Core;
using Microsoft.Graph;
using MyloMail.Api.Domain;
using MyloMail.Api.Providers.Graph;
using Xunit;

namespace MyloMail.Api.Tests.Providers;

public sealed class GraphTopologyTests
{
	[Fact]
	public async Task Topology_walks_every_page_and_child_delta_then_resumes_each_stream()
	{
		var handler = new TopologyHandler();
		var authenticationProvider = GraphMailProvider.CreateAuthenticationProvider(
			new StaticCredential()
		);
		using var http = GraphClientFactory.Create(
			authenticationProvider,
			handlers: [new GraphImmutableIdHandler()],
			finalHandler: handler,
			disposeHandler: false
		);
		using var client = new GraphServiceClient(http, authenticationProvider);

		var baseline = await GraphMailProvider.ReadMailboxTopologyAsync(
			client,
			null,
			new Dictionary<string, SpecialUse>(),
			CancellationToken.None
		);

		Assert.True(baseline.IsFullSnapshot);
		Assert.NotNull(baseline.Cursor);
		Assert.Empty(baseline.RemovedProviderMailboxIds);
		Assert.Equal(
			["A", "B", "C", "E", "F", "G", "H", "I"],
			baseline.Upserted.Select(folder => folder.ProviderMailboxId).Order()
		);
		Assert.Equal("A", baseline.Upserted.Single(folder => folder.ProviderMailboxId == "C").ParentProviderMailboxId);

		var delta = await GraphMailProvider.ReadMailboxTopologyAsync(
			client,
			baseline.Cursor,
			new Dictionary<string, SpecialUse>(),
			CancellationToken.None
		);

		Assert.False(delta.IsFullSnapshot);
		Assert.Equal(
			["A", "C", "D", "G", "I"],
			delta.Upserted.Select(folder => folder.ProviderMailboxId).Order()
		);
		Assert.Equal(["E", "F", "H"], delta.RemovedProviderMailboxIds.Order());
		Assert.Equal(
			"D",
			delta.Upserted.Single(folder => folder.ProviderMailboxId == "C").ParentProviderMailboxId
		);
		Assert.Equal(
			"D",
			delta.Upserted.Single(folder => folder.ProviderMailboxId == "G").ParentProviderMailboxId
		);
		Assert.Equal(
			"D",
			delta.Upserted.Single(folder => folder.ProviderMailboxId == "I").ParentProviderMailboxId
		);
		Assert.DoesNotContain(handler.Requests, request =>
			request.Uri.EndsWith("/topology/e-cursor", StringComparison.Ordinal)
		);
		Assert.Contains(
			handler.Requests,
			request => request.Uri.Contains("/mailFolders/D/childFolders/delta", StringComparison.Ordinal)
		);
		Assert.Single(
			handler.Requests,
			request => request.Uri.Contains(
				"/mailFolders/G/childFolders/delta()",
				StringComparison.Ordinal
			)
		);
		Assert.All(
			handler.Requests,
			request => Assert.Contains("IdType=\"ImmutableId\"", request.Prefer)
		);
	}

	private sealed class TopologyHandler : HttpMessageHandler
	{
		public List<CapturedRequest> Requests { get; } = [];

		protected override Task<HttpResponseMessage> SendAsync(
			HttpRequestMessage request,
			CancellationToken ct
		)
		{
			var uri = request.RequestUri!.AbsoluteUri;
			Requests.Add(new CapturedRequest(uri, [.. request.Headers.GetValues("Prefer")]));
			var path = request.RequestUri.AbsolutePath;
			if (path is "/v1.0/me/mailFolders/E" or "/v1.0/me/mailFolders/F" or "/v1.0/me/mailFolders/H")
			{
				return Task.FromResult(
					new HttpResponseMessage(HttpStatusCode.NotFound)
					{
						Content = new StringContent(
							"{\"error\":{\"code\":\"ErrorItemNotFound\",\"message\":\"not found\"}}",
							Encoding.UTF8,
							"application/json"
						),
					}
				);
			}
			var json = path switch
			{
				"/v1.0/me/mailFolders/delta()" => Page(
					"[{\"id\":\"A\",\"displayName\":\"Alpha\",\"parentFolderId\":null}]",
					nextLink: "https://graph.microsoft.com/v1.0/topology/root-page-2"
				),
				"/v1.0/topology/root-page-2" => Page(
					"[{\"id\":\"B\",\"displayName\":\"Beta\",\"parentFolderId\":null},{\"id\":\"F\",\"displayName\":\"Final removal\",\"parentFolderId\":null},{\"id\":\"H\",\"displayName\":\"Deleted parent\",\"parentFolderId\":null}]",
					deltaLink: "https://graph.microsoft.com/v1.0/topology/root-cursor"
				),
				"/v1.0/me/mailFolders/A/childFolders/delta()" => Page(
					"[{\"id\":\"C\",\"displayName\":\"Child\",\"parentFolderId\":\"A\"},{\"id\":\"E\",\"displayName\":\"Removed child\",\"parentFolderId\":\"A\"},{\"id\":\"G\",\"displayName\":\"Moved grandchild\",\"parentFolderId\":\"A\"}]",
					deltaLink: "https://graph.microsoft.com/v1.0/topology/a-cursor"
				),
				"/v1.0/me/mailFolders/B/childFolders/delta()" => Page(
					"[]",
					deltaLink: "https://graph.microsoft.com/v1.0/topology/b-cursor"
				),
				"/v1.0/me/mailFolders/C/childFolders/delta()" => Page(
					"[]",
					deltaLink: "https://graph.microsoft.com/v1.0/topology/c-cursor"
				),
				"/v1.0/me/mailFolders/E/childFolders/delta()" => Page(
					"[]",
					deltaLink: "https://graph.microsoft.com/v1.0/topology/e-cursor"
				),
				"/v1.0/me/mailFolders/F/childFolders/delta()" => Page(
					"[]",
					deltaLink: "https://graph.microsoft.com/v1.0/topology/f-cursor"
				),
				"/v1.0/me/mailFolders/G/childFolders/delta()" => Page(
					"[]",
					deltaLink: "https://graph.microsoft.com/v1.0/topology/g-cursor"
				),
				"/v1.0/me/mailFolders/H/childFolders/delta()" => Page(
					"[{\"id\":\"I\",\"displayName\":\"Escaped child\",\"parentFolderId\":\"H\"}]",
					deltaLink: "https://graph.microsoft.com/v1.0/topology/h-cursor"
				),
				"/v1.0/me/mailFolders/I/childFolders/delta()" => Page(
					"[]",
					deltaLink: "https://graph.microsoft.com/v1.0/topology/i-cursor"
				),
				"/v1.0/topology/root-cursor" => Page(
					"[{\"id\":\"A\",\"displayName\":\"Alpha renamed\",\"parentFolderId\":null},{\"id\":\"F\",\"displayName\":\"Updated then deleted\",\"parentFolderId\":null},{\"id\":\"F\",\"@removed\":{\"reason\":\"deleted\"}},{\"id\":\"H\",\"@removed\":{\"reason\":\"deleted\"}}]",
					deltaLink: "https://graph.microsoft.com/v1.0/topology/root-cursor-2"
				),
				"/v1.0/topology/a-cursor" => Page(
					"[{\"id\":\"C\",\"@removed\":{\"reason\":\"deleted\"}},{\"id\":\"E\",\"@removed\":{\"reason\":\"deleted\"}},{\"id\":\"G\",\"@removed\":{\"reason\":\"deleted\"}}]",
					deltaLink: "https://graph.microsoft.com/v1.0/topology/a-cursor-2"
				),
				"/v1.0/topology/b-cursor" => Page(
					"[{\"id\":\"C\",\"displayName\":\"Moved child\",\"parentFolderId\":\"B\"},{\"id\":\"D\",\"displayName\":\"Delta\",\"parentFolderId\":\"B\"}]",
					deltaLink: "https://graph.microsoft.com/v1.0/topology/b-cursor-2"
				),
				"/v1.0/topology/c-cursor" => Page(
					"[]",
					deltaLink: "https://graph.microsoft.com/v1.0/topology/c-cursor-2"
				),
				"/v1.0/me/mailFolders/D/childFolders/delta()" => Page(
					"[{\"id\":\"G\",\"displayName\":\"Moved grandchild\",\"parentFolderId\":\"D\"}]",
					deltaLink: "https://graph.microsoft.com/v1.0/topology/d-cursor"
				),
				"/v1.0/me/mailFolders/C" =>
					"{\"id\":\"C\",\"displayName\":\"Moved child\",\"parentFolderId\":\"D\"}",
				"/v1.0/me/mailFolders/G" =>
					"{\"id\":\"G\",\"displayName\":\"Moved grandchild\",\"parentFolderId\":\"D\"}",
				"/v1.0/me/mailFolders/I" =>
					"{\"id\":\"I\",\"displayName\":\"Escaped child\",\"parentFolderId\":\"D\"}",
				_ => throw new InvalidOperationException($"Unexpected Graph topology request: {uri}"),
			};

			return Task.FromResult(
				new HttpResponseMessage(HttpStatusCode.OK)
				{
					Content = new StringContent(json, Encoding.UTF8, "application/json"),
				}
			);
		}

		private static string Page(string value, string? nextLink = null, string? deltaLink = null)
		{
			var link = nextLink is not null
				? $",\"@odata.nextLink\":\"{nextLink}\""
				: $",\"@odata.deltaLink\":\"{deltaLink}\"";
			return $"{{\"value\":{value}{link}}}";
		}
	}

	private sealed record CapturedRequest(string Uri, IReadOnlyList<string> Prefer);

	private sealed class StaticCredential : TokenCredential
	{
		public override AccessToken GetToken(
			TokenRequestContext requestContext,
			CancellationToken ct
		) => new("test-token", DateTimeOffset.UtcNow.AddHours(1));

		public override ValueTask<AccessToken> GetTokenAsync(
			TokenRequestContext requestContext,
			CancellationToken ct
		) => new(GetToken(requestContext, ct));
	}
}
