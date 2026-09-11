using MailKit;
using MailKit.Net.Imap;
using MailKit.Security;
using MimeKit;
using MyloMail.Api.Domain;
using MyloMail.Api.Providers;
using MyloMail.Api.Providers.Contracts;
using MyloMail.Api.Providers.Imap;

namespace MyloMail.Api.Tests.Conformance;

/// <summary>
/// Runs the shared conformance suite against a real IMAP server at one capability tier.
/// </summary>
/// <remarks>
/// The servers come from <c>tests/imap-matrix</c> (<c>pnpm imap:up</c>). Absent, every case
/// skips rather than fails (§11).
/// </remarks>
public sealed class ImapConformanceHarness : IConformanceHarness, IProviderMailboxResolver
{
	private const string SourceFolder = "INBOX";

	private readonly ImapConnectionSettings settings;
	private readonly Dictionary<Guid, string> folders = [];
	private readonly ImapMailProvider provider;

	private ImapConformanceHarness(ImapConnectionSettings settings, string destinationFolder)
	{
		this.settings = settings;
		provider = new ImapMailProvider(settings, this);

		Account = new Account
		{
			Id = Guid.NewGuid(),
			DisplayName = "IMAP conformance",
			ProviderType = ProviderType.Imap,
			AuthState = AuthState.Connected,
		};

		Source = Register(SourceFolder, SpecialUse.Inbox);
		Destination = Register(destinationFolder, SpecialUse.Archive);
	}

	public static async Task<ImapConformanceHarness> CreateAsync(
		ImapConnectionSettings settings,
		CancellationToken ct = default
	)
	{
		// The delimiter and any namespace prefix are server-declared, so the destination
		// folder's real name has to be read from the server rather than assembled here (§1).
		using var client = new ImapClient();
		client.ServerCertificateValidationCallback = static (_, _, _, _) => true;
		await client.ConnectAsync(settings.Host, settings.Port, SocketOptions(settings.ImapSecurity), ct);
		await AuthenticateAsync(client, settings, ct);

		var archive = (await client.GetFoldersAsync(client.PersonalNamespaces[0], cancellationToken: ct))
			.First(f => f.Attributes.HasFlag(FolderAttributes.Archive));

		var destination = archive.FullName;
		await client.DisconnectAsync(true, ct);

		return new ImapConformanceHarness(settings, destination);
	}

	private Mailbox Register(string providerId, SpecialUse specialUse)
	{
		var mailbox = new Mailbox
		{
			Id = Guid.NewGuid(),
			AccountId = Account.Id,
			ProviderMailboxId = providerId,
			Name = providerId,
			SpecialUse = specialUse,
		};

		folders[mailbox.Id] = providerId;
		return mailbox;
	}

	public IMailProvider Provider => provider;

	/// <summary>How to reach the same server, for a test that builds its own provider.</summary>
	public ImapConnectionSettings Settings => settings;

	public Account Account { get; }

	public Mailbox Source { get; }

	public Mailbox Destination { get; }

	public string ProviderMailboxId(Guid mailboxId) => folders[mailboxId];

	public string LocalPath(Guid mailboxId, char separator) => folders[mailboxId];

	public async Task<MessageOccurrenceRef> SeedMessageAsync(
		Mailbox mailbox,
		CancellationToken ct = default
	)
	{
		var uid = await AppendAsync(mailbox, ct);
		return new MessageOccurrenceRef(Guid.NewGuid(), mailbox.Id, uid.Id.ToString());
	}

	private async Task<UniqueId> AppendAsync(Mailbox mailbox, CancellationToken ct)
	{
		using var client = await OpenClientAsync(ct);
		var folder = await client.GetFolderAsync(ProviderMailboxId(mailbox.Id), ct);
		await folder.OpenAsync(FolderAccess.ReadWrite, ct);

		var message = new MimeMessage
		{
			Subject = $"Conformance {Guid.NewGuid():N}",
			Body = new TextPart("plain") { Text = "seeded by the conformance suite" },
		};
		message.From.Add(new MailboxAddress("Conformance", "conformance@mylomail.local"));
		message.To.Add(new MailboxAddress("Test", settings.UserName));

		var appended = await folder.AppendAsync(message, MessageFlags.None, ct);
		await client.DisconnectAsync(true, ct);

		// Without UIDPLUS the server does not report the new UID, so fall back to the highest
		// UID present — adequate for a single-threaded test, not for production.
		return appended ?? await HighestUidAsync(mailbox, ct);
	}

	private async Task<UniqueId> HighestUidAsync(Mailbox mailbox, CancellationToken ct)
	{
		using var client = await OpenClientAsync(ct);
		var folder = await client.GetFolderAsync(ProviderMailboxId(mailbox.Id), ct);
		await folder.OpenAsync(FolderAccess.ReadOnly, ct);
		var uids = await folder.SearchAsync(MailKit.Search.SearchQuery.All, ct);
		await client.DisconnectAsync(true, ct);
		return uids.OrderBy(u => u.Id).Last();
	}

	private async Task<ImapClient> OpenClientAsync(CancellationToken ct)
	{
		var client = new ImapClient();
		client.ServerCertificateValidationCallback = static (_, _, _, _) => true;
		await client.ConnectAsync(settings.Host, settings.Port, SocketOptions(settings.ImapSecurity), ct);
		await AuthenticateAsync(client, settings, ct);
		return client;
	}

	private static Task AuthenticateAsync(
		ImapClient client,
		ImapConnectionSettings settings,
		CancellationToken ct
	) =>
		settings.AuthMethod == ImapAuthMethod.OAuth2
			? client.AuthenticateAsync(
				new SaslMechanismOAuth2(settings.UserName, settings.Password),
				ct
			)
			: client.AuthenticateAsync(settings.UserName, settings.Password, ct);

	private static SecureSocketOptions SocketOptions(MailTransportSecurity security) =>
		security switch
		{
			MailTransportSecurity.None => SecureSocketOptions.None,
			MailTransportSecurity.TlsOnConnect => SecureSocketOptions.SslOnConnect,
			MailTransportSecurity.StartTls => SecureSocketOptions.StartTls,
			_ => throw new ArgumentOutOfRangeException(nameof(security), security, null),
		};

	public Task<(MessageOccurrenceRef First, MessageOccurrenceRef Second)> SeedSharedMessageAsync(
		Mailbox first,
		Mailbox second,
		CancellationToken ct = default
	) =>
		// IMAP has exactly one membership per message (§1), so the capability is false and no
		// case calls this.
		throw new NotSupportedException("IMAP messages belong to exactly one folder");

	public async Task SeedPageOverflowAsync(Mailbox mailbox, CancellationToken ct = default)
	{
		// IMAP advances its cursor mid-walk, so no case calls this either; implemented rather
		// than thrown because it is cheap and the capability could change.
		for (var i = 0; i < 3; i++)
		{
			await AppendAsync(mailbox, ct);
		}
	}

	public async Task<ProviderCursorState> BaselineCursorAsync(
		Mailbox mailbox,
		CancellationToken ct = default
	)
	{
		using var client = await OpenClientAsync(ct);
		var folder = await client.GetFolderAsync(ProviderMailboxId(mailbox.Id), ct);
		await folder.OpenAsync(FolderAccess.ReadOnly, ct);
		var cursor = new ImapUidCursor(folder.UidValidity, 0, null, null);
		await client.DisconnectAsync(true, ct);
		return cursor;
	}

	public async Task<ProviderCursorState> ExpiredCursorAsync(
		Mailbox mailbox,
		CancellationToken ct = default
	)
	{
		// A UIDVALIDITY that does not match is precisely what a server-side reindex looks
		// like, and every stored UID becomes meaningless (§3).
		var current = (ImapUidCursor)await BaselineCursorAsync(mailbox, ct);
		return current with { UidValidity = current.UidValidity + 1 };
	}

	public Task<MessageOccurrenceRef> UnresolvableOccurrenceAsync(
		Mailbox mailbox,
		CancellationToken ct = default
	) => Task.FromResult(new MessageOccurrenceRef(Guid.NewGuid(), mailbox.Id, "4294967000"));

	public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
