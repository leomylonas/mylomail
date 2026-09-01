using Google.Apis.Auth.OAuth2;
using Google.Apis.Gmail.v1;
using Google.Apis.Gmail.v1.Data;
using Google.Apis.Services;
using MyloMail.Api.Credentials;
using MyloMail.Api.Domain;
using MyloMail.Api.Providers;
using MyloMail.Api.Providers.Contracts;
using MyloMail.Api.Providers.Gmail;
using GmailMessage = Google.Apis.Gmail.v1.Data.Message;

namespace MyloMail.Api.Tests.Conformance;

/// <summary>Real Gmail conformance subject. It is intentionally skipped without local OAuth configuration.</summary>
public sealed class GmailConformanceTests : MailProviderConformanceTests
{
	private static string? ClientId => Environment.GetEnvironmentVariable("GMAIL_CLIENT_ID");

	private static string? ClientSecret => Environment.GetEnvironmentVariable("GMAIL_CLIENT_SECRET");

	protected override string? SkipReason =>
		string.IsNullOrWhiteSpace(ClientId) || string.IsNullOrWhiteSpace(ClientSecret)
			? "GMAIL_CLIENT_ID/GMAIL_CLIENT_SECRET not set — source .dev/provider-test.env"
			: null;

	protected override async Task<IConformanceHarness> CreateHarnessAsync() =>
		await GmailConformanceHarness.CreateAsync(ClientId!, ClientSecret!);
}

public sealed class GmailConformanceHarness : IConformanceHarness, IProviderMailboxResolver
{
	private static readonly InMemoryCredentialStore SharedCredentials = new();
	private static readonly Guid SharedAccountId = Guid.NewGuid();
	private static readonly SemaphoreSlim AuthorizationGate = new(1, 1);
	private readonly GmailOAuthAuthenticator oauth;
	private readonly GmailService service;
	private readonly Dictionary<Guid, string> providerMailboxIds = [];
	private readonly List<string> createdLabels = [];
	private readonly List<string> createdMessages = [];
	private readonly string emailAddress;

	private GmailConformanceHarness(
		GmailOAuthAuthenticator oauth,
		GmailService service,
		string emailAddress
	)
	{
		this.oauth = oauth;
		this.service = service;
		this.emailAddress = emailAddress;
		Account = new Account
		{
			Id = SharedAccountId,
			DisplayName = "Gmail conformance",
			ProviderType = ProviderType.Gmail,
			AuthState = AuthState.Connected,
		};
		Provider = new GmailMailProvider(oauth, this);
		Source = NewMailbox("source");
		Destination = NewMailbox("destination");
	}

	public IMailProvider Provider { get; }

	public bool CanProduceExpiredCursor => false;

	public Account Account { get; }

	public Mailbox Source { get; }

	public Mailbox Destination { get; }

	public static async Task<GmailConformanceHarness> CreateAsync(string clientId, string clientSecret)
	{
		var credentials = SharedCredentials;
		var oauth = new GmailOAuthAuthenticator(
			credentials,
			new ClientSecrets { ClientId = clientId, ClientSecret = clientSecret }
		);
		var bootstrapAccount = new Account { Id = SharedAccountId, ProviderType = ProviderType.Gmail };
		await AuthorizationGate.WaitAsync();
		UserCredential credential;
		try
		{
			credential = await oauth.AuthorizeAsync(bootstrapAccount, CancellationToken.None);
		}
		finally
		{
			AuthorizationGate.Release();
		}
		var service = new GmailService(
			new BaseClientService.Initializer
			{
				HttpClientInitializer = credential,
				ApplicationName = "MyloMail conformance",
			}
		);
		var profile = await service.Users.GetProfile("me").ExecuteAsync();
		var harness = new GmailConformanceHarness(oauth, service, profile.EmailAddress!);

		await harness.CreateLabelsAsync();
		return harness;
	}

	public async Task<MessageOccurrenceRef> SeedMessageAsync(Mailbox mailbox, CancellationToken ct = default)
	{
		var sent = await service.Users.Messages
			.Send(new GmailMessage { Raw = RawMessage("seed") }, "me")
			.ExecuteAsync(ct);
		var id = sent.Id ?? throw new InvalidOperationException("Gmail did not return the seeded message id.");
		createdMessages.Add(id);
		await service.Users.Messages
			.Modify(
				new ModifyMessageRequest { AddLabelIds = [ProviderMailboxId(mailbox.Id)] },
				"me",
				id
			)
			.ExecuteAsync(ct);
		return new MessageOccurrenceRef(Guid.NewGuid(), mailbox.Id, id);
	}

	public async Task<(MessageOccurrenceRef First, MessageOccurrenceRef Second)> SeedSharedMessageAsync(
		Mailbox first,
		Mailbox second,
		CancellationToken ct = default
	)
	{
		var reference = await SeedMessageAsync(first, ct);
		await service.Users.Messages
			.Modify(
				new ModifyMessageRequest { AddLabelIds = [ProviderMailboxId(second.Id)] },
				"me",
				reference.ProviderOccurrenceId
			)
			.ExecuteAsync(ct);
		return (
			reference,
			new MessageOccurrenceRef(reference.MessageId, second.Id, reference.ProviderOccurrenceId)
		);
	}

	public async Task SeedPageOverflowAsync(Mailbox mailbox, CancellationToken ct = default)
	{
		for (var i = 0; i < 210; i++)
		{
			await SeedMessageAsync(mailbox, ct);
		}
	}

	public async Task<ProviderCursorState> BaselineCursorAsync(Mailbox mailbox, CancellationToken ct = default)
	{
		var profile = await service.Users.GetProfile("me").ExecuteAsync(ct);
		return new GmailHistoryCursor(profile.HistoryId!.ToString()!);
	}

	public Task<ProviderCursorState> ExpiredCursorAsync(Mailbox mailbox, CancellationToken ct = default) =>
		Task.FromResult<ProviderCursorState>(new GmailHistoryCursor("1"));

	public async Task<MessageOccurrenceRef> UnresolvableOccurrenceAsync(
		Mailbox mailbox,
		CancellationToken ct = default
	)
	{
		var reference = await SeedMessageAsync(mailbox, ct);
		var id = reference.ProviderOccurrenceId;
		var replacement = id[^1] == '0' ? '1' : '0';
		return reference with { ProviderOccurrenceId = $"{id[..^1]}{replacement}" };
	}

	public string ProviderMailboxId(Guid mailboxId) => providerMailboxIds[mailboxId];

	public string LocalPath(Guid mailboxId, char separator) => providerMailboxIds[mailboxId];

	public async ValueTask DisposeAsync()
	{
		foreach (var id in createdMessages)
		{
			try
			{
				await service.Users.Messages.Delete("me", id).ExecuteAsync();
			}
			catch (Google.GoogleApiException)
			{
				// A test may already have permanently deleted the message.
			}
		}

		foreach (var id in createdLabels)
		{
			try
			{
				await service.Users.Labels.Delete("me", id).ExecuteAsync();
			}
			catch (Google.GoogleApiException)
			{
				// The label may already have been removed during a failed test.
			}
		}
	}

	private Mailbox NewMailbox(string name) =>
		new()
		{
			Id = Guid.NewGuid(),
			AccountId = Account.Id,
			Name = name,
		};

	private async Task CreateLabelsAsync()
	{
		foreach (var mailbox in new[] { Source, Destination })
		{
			var label = await service.Users.Labels
				.Create(
					new Label
					{
						Name = $"MyloMail conformance {Guid.NewGuid():N}",
						LabelListVisibility = "labelShow",
					},
					"me"
				)
				.ExecuteAsync();
			mailbox.ProviderMailboxId = label.Id;
			providerMailboxIds[mailbox.Id] = label.Id!;
			createdLabels.Add(label.Id!);
		}
	}

	private string RawMessage(string subject)
	{
		var message = $"To: {emailAddress}\r\nSubject: {subject}\r\n\r\nMyloMail conformance";
		return Convert
			.ToBase64String(System.Text.Encoding.UTF8.GetBytes(message))
			.TrimEnd('=')
			.Replace('+', '-')
			.Replace('/', '_');
	}
}
