using System.Net;
using System.Text;
using System.Text.Json;
using Azure.Core;
using Microsoft.Extensions.Options;
using Microsoft.Graph;
using Microsoft.Kiota.Abstractions;
using Microsoft.Kiota.Abstractions.Authentication;
using MyloMail.Api.Credentials;
using MyloMail.Api.Domain;
using MyloMail.Api.Errors;
using MyloMail.Api.Providers;
using MyloMail.Api.Providers.Contracts;
using MyloMail.Api.Providers.Graph;
using Xunit;

namespace MyloMail.Api.Tests.Providers;

public sealed class GraphSharedMailboxTests
{
	private const string Shared = "shared@contoso.com";

	private static Account AccountWith(string? sharedMailbox) =>
		new()
		{
			ProviderType = ProviderType.Microsoft365,
			ProviderConfig = new Microsoft365ProviderConfig { SharedMailbox = sharedMailbox },
		};

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	public void An_account_without_a_shared_mailbox_addresses_its_own_mailbox(string? configured)
	{
		var mailbox = GraphMailbox.For(AccountWith(configured));

		Assert.False(mailbox.IsShared);
		Assert.Equal(GraphMailbox.Personal, mailbox);
	}

	[Fact]
	public void An_account_without_any_microsoft_config_addresses_its_own_mailbox() =>
		Assert.False(GraphMailbox.For(new Account { ProviderType = ProviderType.Microsoft365 }).IsShared);

	[Fact]
	public void A_configured_shared_mailbox_is_the_target()
	{
		var mailbox = GraphMailbox.For(AccountWith(Shared));

		Assert.True(mailbox.IsShared);
		Assert.Equal(Shared, mailbox.SharedMailbox);
	}

	[Theory]
	[InlineData("https://graph.microsoft.com/v1.0/me/mailFolders?$select=id", "https://graph.microsoft.com/v1.0/users/shared%40contoso.com/mailFolders?$select=id")]
	[InlineData("https://graph.microsoft.com/v1.0/me", "https://graph.microsoft.com/v1.0/users/shared%40contoso.com")]
	[InlineData("https://graph.microsoft.com/beta/me/messages/abc=", "https://graph.microsoft.com/beta/users/shared%40contoso.com/messages/abc=")]
	[InlineData("https://graph.microsoft.com/v1.0/ME/events", "https://graph.microsoft.com/v1.0/users/shared%40contoso.com/events")]
	public void A_shared_mailbox_retargets_me_to_users(string original, string expected) =>
		Assert.Equal(new Uri(expected), new GraphMailbox(Shared).Retarget(new Uri(original)));

	[Theory]
	[InlineData("https://graph.microsoft.com/v1.0/$batch")]
	[InlineData("https://graph.microsoft.com/v1.0/mexico/messages")]
	[InlineData("https://graph.microsoft.com/v1.0/users/shared%40contoso.com/messages")]
	[InlineData("https://graph.microsoft.com/v1.0/users('abc')/mailFolders/delta?$deltatoken=/me")]
	[InlineData("https://graph.microsoft.com/v1.0?next=/me")]
	public void Only_requests_addressed_to_me_are_retargeted(string original)
	{
		var uri = new Uri(original);

		Assert.Same(uri, new GraphMailbox(Shared).Retarget(uri));
	}

	[Fact]
	public void A_personal_mailbox_never_retargets()
	{
		var uri = new Uri("https://graph.microsoft.com/v1.0/me/messages");

		Assert.Same(uri, GraphMailbox.Personal.Retarget(uri));
	}

	[Fact]
	public void The_personal_pipeline_is_only_the_immutable_id_handler()
	{
		Assert.IsType<GraphImmutableIdHandler>(Assert.Single(GraphMailbox.Personal.Handlers()));
		var shared = new GraphMailbox(Shared).Handlers();
		Assert.IsType<GraphImmutableIdHandler>(shared[0]);
		Assert.IsType<GraphSharedMailboxHandler>(shared[1]);
	}

	/// <summary>
	/// The seam must produce the request the generated <c>Users[id]</c> builder would have, or a
	/// shared account would address something Graph does not recognise.
	/// </summary>
	[Fact]
	public async Task A_me_builder_request_is_sent_where_the_users_builder_would_send_it()
	{
		var viaSeam = new CapturingHandler();
		var viaUsers = new CapturingHandler();
		using var seamClient = Client(new GraphMailbox(Shared), viaSeam);
		using var usersClient = Client(GraphMailbox.Personal, viaUsers);

		await seamClient.Me.MailFolders["inbox"].Messages.GetAsync(c => c.QueryParameters.Select = ["id"]);
		await usersClient.Users[Shared].MailFolders["inbox"].Messages.GetAsync(c => c.QueryParameters.Select = ["id"]);

		var seam = Assert.Single(viaSeam.Requests);
		var users = Assert.Single(viaUsers.Requests);
		Assert.StartsWith("/v1.0/users/", seam.Uri.AbsolutePath);
		Assert.Equal(Uri.UnescapeDataString(users.Uri.AbsoluteUri), Uri.UnescapeDataString(seam.Uri.AbsoluteUri));
		Assert.Contains("IdType=\"ImmutableId\"", seam.Prefer);
	}

	[Fact]
	public async Task A_personal_account_still_sends_to_me()
	{
		var capture = new CapturingHandler();
		using var client = Client(GraphMailbox.Personal, capture);

		await client.Me.MailFolders["inbox"].Messages.GetAsync();

		var request = Assert.Single(capture.Requests);
		Assert.StartsWith("/v1.0/me/", request.Uri.AbsolutePath);
		Assert.Contains("IdType=\"ImmutableId\"", request.Prefer);
	}

	[Fact]
	public async Task Batch_sub_requests_are_retargeted_like_the_users_builder_would()
	{
		using var client = Client(new GraphMailbox(Shared), new CapturingHandler());
		var request = client.Me.Messages["message-one"].Move.ToPostRequestInformation(
			new Microsoft.Graph.Me.Messages.Item.Move.MovePostRequestBody { DestinationId = "archive" }
		);
		var expected = client.Users[Shared].Messages["message-one"].Move.ToPostRequestInformation(
			new Microsoft.Graph.Users.Item.Messages.Item.Move.MovePostRequestBody { DestinationId = "archive" }
		);

		new GraphMailbox(Shared).Retarget(request);

		var actualUri = (await client.RequestAdapter.ConvertToNativeRequestAsync<HttpRequestMessage>(request))!.RequestUri;
		var expectedUri = (await client.RequestAdapter.ConvertToNativeRequestAsync<HttpRequestMessage>(expected))!.RequestUri;
		Assert.Equal(expectedUri, actualUri);
	}

	[Fact]
	public async Task Batch_sub_requests_of_a_personal_account_are_untouched()
	{
		using var client = Client(GraphMailbox.Personal, new CapturingHandler());
		var request = client.Me.Messages["message-one"].ToDeleteRequestInformation();
		var template = request.UrlTemplate;

		GraphMailbox.Personal.Retarget(request);

		Assert.Equal(template, request.UrlTemplate);
		var uri = (await client.RequestAdapter.ConvertToNativeRequestAsync<HttpRequestMessage>(request))!.RequestUri;
		Assert.StartsWith("/v1.0/me/messages/", uri!.AbsolutePath);
	}

	[Fact]
	public async Task A_shared_batch_addresses_the_shared_mailbox_inside_the_batch_body()
	{
		var handler = new BatchDeleteHandler();
		using var client = new GraphServiceClient(new HttpClient(handler), new AnonymousAuthenticationProvider());
		var references = new MessageOccurrenceRef[]
		{
			new(Guid.NewGuid(), Guid.NewGuid(), "message-one", Guid.NewGuid()),
			new(Guid.NewGuid(), Guid.NewGuid(), "message-two", Guid.NewGuid()),
		};

		var result = await GraphMailProvider.DeletePermanentlyBatchAsync(
			client,
			new GraphMailbox(Shared),
			references,
			CancellationToken.None
		);

		Assert.All(result.Items, item => Assert.True(item.Succeeded));
		Assert.Equal("/v1.0/$batch", handler.OuterPath);
		Assert.Equal(2, handler.SubRequestUrls.Count);
		Assert.All(
			handler.SubRequestUrls,
			url => Assert.StartsWith("/users/shared@contoso.com/messages/", Uri.UnescapeDataString(url))
		);
	}

	[Theory]
	[InlineData("ErrorSendAsDenied", "Send permission denied")]
	[InlineData("ErrorSendOnBehalfOfDenied", "Send permission denied")]
	[InlineData("ErrorAccessDenied", "Shared mailbox access denied")]
	[InlineData(null, "Shared mailbox access denied")]
	public async Task Forbidden_on_a_shared_mailbox_becomes_a_mapped_provider_rejection(string? code, string title)
	{
		var body = code is null
			? string.Empty
			: JsonSerializer.Serialize(new { error = new { code, message = "denied" } });
		using var client = Client(
			new GraphMailbox(Shared),
			new CapturingHandler(HttpStatusCode.Forbidden, body)
		);

		var exception = await Assert.ThrowsAsync<ProviderAuthenticationException>(() =>
			client.Me.Messages["draft"].Send.PostAsync()
		);

		var problem = Assert.IsType<MutationProblemDetails>(exception.Problem);
		Assert.Equal(ErrorCategory.ProviderRejected, problem.Category);
		Assert.Equal(title, problem.Title);
		Assert.Equal(403, problem.Status);
		Assert.Equal(code ?? "403", problem.ProviderCode);
		Assert.Equal(AuthState.Error, exception.AccountState);
	}

	[Fact]
	public async Task Other_failures_on_a_shared_mailbox_are_left_to_the_existing_handling()
	{
		using var client = Client(
			new GraphMailbox(Shared),
			new CapturingHandler(HttpStatusCode.NotFound, "{}")
		);

		await Assert.ThrowsAnyAsync<ApiException>(() => client.Me.Messages["draft"].GetAsync());
	}

	[Fact]
	public async Task Forbidden_on_a_personal_account_is_not_translated()
	{
		using var client = Client(
			GraphMailbox.Personal,
			new CapturingHandler(HttpStatusCode.Forbidden, "{}")
		);

		await Assert.ThrowsAnyAsync<ApiException>(() => client.Me.Messages["draft"].GetAsync());
	}

	[Fact]
	public void A_personal_account_requests_the_plain_scopes()
	{
		Assert.Same(GraphOAuthAuthenticator.Scopes, GraphOAuthAuthenticator.ScopesFor(AccountWith(null)));
		Assert.Contains("Contacts.ReadWrite", GraphOAuthAuthenticator.Scopes);
	}

	[Fact]
	public void A_shared_account_requests_only_the_shared_scopes()
	{
		var scopes = GraphOAuthAuthenticator.ScopesFor(AccountWith(Shared));

		Assert.Equal(
			["Calendars.ReadWrite.Shared", "Mail.ReadWrite.Shared", "Mail.Send.Shared"],
			scopes.Order()
		);
		Assert.All(scopes, scope => Assert.EndsWith(".Shared", scope));
	}

	[Theory]
	[InlineData("support@contoso.com", true)]
	[InlineData("first.last+tag@sub.contoso.co.uk", true)]
	[InlineData("o'brien@contoso.com", true)]
	[InlineData("", false)]
	[InlineData("support", false)]
	[InlineData("support@contoso", false)]
	[InlineData("support@@contoso.com", false)]
	[InlineData("two words@contoso.com", false)]
	[InlineData("sup/port@contoso.com", false)]
	[InlineData("support@contoso.com/../me", false)]
	[InlineData("support@contoso.com?x=1", false)]
	[InlineData("Support <support@contoso.com>", false)]
	public void Only_plain_email_addresses_are_valid_shared_mailboxes(string value, bool expected) =>
		Assert.Equal(expected, GraphMailbox.IsValidSharedMailbox(value));

	[Fact]
	public void A_shared_mailbox_has_no_remote_contact_provider()
	{
		var factory = ContactFactory();

		Assert.IsType<LocalContactProvider>(factory.For(AccountWith(Shared)));
		Assert.IsType<GraphContactProvider>(factory.For(AccountWith(null)));
		Assert.True(LocalContactProvider.IsLocalOnly(AccountWith(Shared)));
		Assert.False(LocalContactProvider.IsLocalOnly(AccountWith(null)));
	}

	[Fact]
	public async Task A_shared_mailbox_contact_pull_yields_nothing_to_sync()
	{
		var account = AccountWith(Shared);
		var pull = await ContactFactory().For(account).PullAsync(account, useCursor: true, CancellationToken.None);

		Assert.Empty(pull.Contacts);
		Assert.Empty(pull.DeletedProviderContactIds);
	}

	private static ContactProviderFactory ContactFactory() =>
		new(
			Options.Create(new ProviderClientOptions { Graph = { ClientId = "client" } }),
			new InMemoryCredentialStore(),
			new StubHttpClientFactory()
		);

	private static GraphServiceClient Client(GraphMailbox mailbox, HttpMessageHandler final)
	{
		var authenticationProvider = GraphMailProvider.CreateAuthenticationProvider(new StaticCredential());
		var http = GraphClientFactory.Create(
			authenticationProvider,
			handlers: mailbox.Handlers(),
			finalHandler: final,
			disposeHandler: false
		);
		return new GraphServiceClient(http, authenticationProvider);
	}

	private sealed record CapturedRequest(Uri Uri, string? Prefer);

	private sealed class CapturingHandler(HttpStatusCode status = HttpStatusCode.OK, string body = "{\"value\":[]}")
		: HttpMessageHandler
	{
		public List<CapturedRequest> Requests { get; } = [];

		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
		{
			Requests.Add(new CapturedRequest(
				request.RequestUri!,
				request.Headers.TryGetValues("Prefer", out var values) ? string.Join(",", values) : null
			));
			return Task.FromResult(new HttpResponseMessage(status)
			{
				Content = new StringContent(body, Encoding.UTF8, "application/json"),
			});
		}
	}

	private sealed class BatchDeleteHandler : HttpMessageHandler
	{
		public string? OuterPath { get; private set; }
		public List<string> SubRequestUrls { get; } = [];

		protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
		{
			OuterPath = request.RequestUri!.AbsolutePath;
			using var payload = JsonDocument.Parse(await request.Content!.ReadAsStreamAsync(ct));
			var entries = payload.RootElement.GetProperty("requests").EnumerateArray().ToList();
			SubRequestUrls.AddRange(entries.Select(entry => entry.GetProperty("url").GetString()!));
			var body = JsonSerializer.Serialize(new
			{
				responses = entries.Select(entry => new
				{
					id = entry.GetProperty("id").GetString(),
					status = 204,
					headers = new Dictionary<string, string>(),
				}),
			});
			return new HttpResponseMessage(HttpStatusCode.OK)
			{
				Content = new StringContent(body, Encoding.UTF8, "application/json"),
			};
		}
	}

	private sealed class StubHttpClientFactory : IHttpClientFactory
	{
		public HttpClient CreateClient(string name) => new();
	}

	private sealed class StaticCredential : TokenCredential
	{
		public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken ct) =>
			new("test-token", DateTimeOffset.UtcNow.AddHours(1));

		public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken ct) =>
			new(GetToken(requestContext, ct));
	}
}
