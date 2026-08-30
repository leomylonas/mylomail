using Microsoft.Graph;
using Microsoft.Graph.Models;
using MyloMail.Api.Credentials;
using MyloMail.Api.Domain;
using MyloMail.Api.Providers;
using MyloMail.Api.Providers.Contracts;
using MyloMail.Api.Providers.Graph;
using DomainMailbox = MyloMail.Api.Domain.Mailbox;

namespace MyloMail.Api.Tests.Conformance;

/// <summary>Real Graph conformance subject. It is intentionally skipped without a local client id.</summary>
public sealed class GraphConformanceTests : MailProviderConformanceTests
{
	private static string? ClientId => Environment.GetEnvironmentVariable("GRAPH_CLIENT_ID");

	private static string Authority =>
		$"https://login.microsoftonline.com/{Environment.GetEnvironmentVariable("GRAPH_TENANT_ID") ?? "common"}";

	protected override string? SkipReason =>
		string.IsNullOrWhiteSpace(ClientId)
			? "GRAPH_CLIENT_ID not set — source .dev/provider-test.env"
			: null;

	protected override async Task<IConformanceHarness> CreateHarnessAsync() =>
		await GraphConformanceHarness.CreateAsync(ClientId!, Authority);
}

public sealed class GraphConformanceHarness : IConformanceHarness
{
	private static readonly InMemoryCredentialStore SharedCredentials = new();
	private static readonly Guid SharedAccountId = Guid.NewGuid();
	private static readonly SemaphoreSlim AuthorizationGate = new(1, 1);
	private readonly GraphServiceClient client;
	private readonly List<string> createdFolders = [];
	private readonly List<string> createdMessages = [];

	private GraphConformanceHarness(GraphMailProvider provider, Account account, GraphServiceClient client)
	{
		Provider = provider;
		Account = account;
		this.client = client;
		Source = NewMailbox("source");
		Destination = NewMailbox("destination");
	}

	public IMailProvider Provider { get; }

	public Account Account { get; }

	public DomainMailbox Source { get; }

	public DomainMailbox Destination { get; }

	public static async Task<GraphConformanceHarness> CreateAsync(string clientId, string authority)
	{
		var credentials = SharedCredentials;
		var oauth = new GraphOAuthAuthenticator(credentials, clientId, authority);
		var account = new Account
		{
			Id = SharedAccountId,
			DisplayName = "Graph conformance",
			ProviderType = ProviderType.Microsoft365,
			AuthState = AuthState.Connected,
		};
		await AuthorizationGate.WaitAsync();
		AuthResult auth;
		try
		{
			auth = await oauth.AuthenticateAsync(account, CancellationToken.None);
		}
		finally
		{
			AuthorizationGate.Release();
		}
		if (!auth.Succeeded)
		{
			throw new InvalidOperationException(auth.Problem?.Detail ?? "Microsoft authentication failed.");
		}

		var credential = new GraphAccountTokenCredential(oauth, account);
		var http = GraphClientFactory.Create(credential, [new GraphImmutableIdHandler()]);
		var client = new GraphServiceClient(http, credential, GraphOAuthAuthenticator.Scopes);
		var harness = new GraphConformanceHarness(new GraphMailProvider(oauth), account, client);
		await harness.CreateFoldersAsync();
		return harness;
	}

	public async Task<MessageOccurrenceRef> SeedMessageAsync(DomainMailbox mailbox, CancellationToken ct = default)
	{
		var message = await client.Me.MailFolders[mailbox.ProviderMailboxId!].Messages.PostAsync(
			new Message { Subject = $"MyloMail conformance {Guid.NewGuid():N}", Body = new ItemBody { Content = "seed" } },
			null,
			ct
		);
		var id = message?.Id ?? throw new InvalidOperationException("Graph did not return the seeded message id.");
		createdMessages.Add(id);
		return new MessageOccurrenceRef(Guid.NewGuid(), mailbox.Id, id);
	}

	public async Task<(MessageOccurrenceRef First, MessageOccurrenceRef Second)> SeedSharedMessageAsync(
		DomainMailbox first,
		DomainMailbox second,
		CancellationToken ct = default
	)
	{
		var reference = await SeedMessageAsync(first, ct);
		return (reference, new MessageOccurrenceRef(reference.MessageId, second.Id, reference.ProviderOccurrenceId));
	}

	public async Task SeedPageOverflowAsync(DomainMailbox mailbox, CancellationToken ct = default)
	{
		for (var i = 0; i < 210; i++)
		{
			await SeedMessageAsync(mailbox, ct);
		}
	}

	public async Task<ProviderCursorState> BaselineCursorAsync(DomainMailbox mailbox, CancellationToken ct = default)
	{
		var delta = client.Me.MailFolders[mailbox.ProviderMailboxId!].Messages.Delta;
		var page = await delta.GetAsDeltaGetResponseAsync(null, ct);
		while (page?.OdataNextLink is not null)
		{
			page = await delta.WithUrl(page.OdataNextLink).GetAsDeltaGetResponseAsync(null, ct);
		}

		return new GraphDeltaCursor(
			page?.OdataDeltaLink
				?? throw new InvalidOperationException("Graph did not return a delta link for the test folder.")
		);
	}

	public Task<ProviderCursorState> ExpiredCursorAsync(DomainMailbox mailbox, CancellationToken ct = default) =>
		Task.FromResult<ProviderCursorState>(
			new GraphDeltaCursor(
				$"https://graph.microsoft.com/v1.0/me/mailFolders/{mailbox.ProviderMailboxId}/messages/delta?$deltatoken=invalid"
			)
		);

	public Task<MessageOccurrenceRef> UnresolvableOccurrenceAsync(DomainMailbox mailbox, CancellationToken ct = default) =>
		Task.FromResult(new MessageOccurrenceRef(Guid.NewGuid(), mailbox.Id, Guid.NewGuid().ToString("N")));

	public async ValueTask DisposeAsync()
	{
		foreach (var id in createdMessages)
		{
			try
			{
				await client.Me.Messages[id].DeleteAsync();
			}
			catch (Microsoft.Kiota.Abstractions.ApiException)
			{
				// The test may already have moved or deleted this message.
			}
		}

		foreach (var id in createdFolders)
		{
			try
			{
				await client.Me.MailFolders[id].DeleteAsync();
			}
			catch (Microsoft.Kiota.Abstractions.ApiException)
			{
				// Cleanup is best effort; the folder may already have gone away.
			}
		}
	}

	private DomainMailbox NewMailbox(string name) =>
		new() { Id = Guid.NewGuid(), AccountId = Account.Id, Name = name };

	private async Task CreateFoldersAsync()
	{
		foreach (var mailbox in new[] { Source, Destination })
		{
			var folder = await client.Me.MailFolders.PostAsync(
				new MailFolder { DisplayName = $"MyloMail conformance {Guid.NewGuid():N}" }
			);
			mailbox.ProviderMailboxId = folder?.Id
				?? throw new InvalidOperationException("Graph did not return the conformance folder id.");
			createdFolders.Add(mailbox.ProviderMailboxId);
		}
	}
}
