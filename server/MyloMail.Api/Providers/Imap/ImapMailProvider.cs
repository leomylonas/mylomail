using System.Net.Sockets;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MailKit.Security;
using MimeKit;
using MyloMail.Api.Domain;
using MyloMail.Api.Errors;
using MyloMail.Api.Providers.Contracts;

namespace MyloMail.Api.Providers.Imap;

/// <summary>
/// IMAP, over MailKit. Thin by design at this stage (§16, stage B): authentication,
/// topology, one read path and one mutation, proven against the shared conformance suite
/// at all three capability tiers before anything is built on top.
/// </summary>
/// <remarks>
/// <para>
/// <b>This instance is scoped to one account.</b> IMAP capabilities are negotiated per
/// session, so a single instance shared across accounts could not answer
/// <see cref="Capabilities"/> honestly — a server at the QRESYNC tier and one at the basic
/// tier are different providers as far as recovery policy is concerned. §2 declares
/// <c>Capabilities</c> as a provider property and resolves implementations by
/// <see cref="ProviderType"/>, which assumes capabilities are a property of the type; for
/// IMAP they are a property of the connection. The factory therefore has to hand out an
/// instance per account.
/// </para>
/// <para>
/// Sessions are opened per operation rather than pooled. Correct but not yet efficient;
/// pooling belongs with the job scheduling in stage C, not here.
/// </para>
/// </remarks>
public sealed partial class ImapMailProvider : IMailProvider
{
	private readonly ImapConnectionSettings settings;
	private readonly IImapMailboxResolver mailboxes;
	private ProviderCapabilities capabilities = ImapCapabilityNegotiation.Unknown;

	public ImapMailProvider(ImapConnectionSettings settings, IImapMailboxResolver mailboxes)
	{
		this.settings = settings;
		this.mailboxes = mailboxes;
	}

	public ProviderType Type => ProviderType.Imap;

	/// <summary>
	/// Negotiated on first connect. Until then this reports the weakest tier, so a caller
	/// reading it early skips no reconciliation it actually needs.
	/// </summary>
	public ProviderCapabilities Capabilities => capabilities;

	private async Task<ImapClient> ConnectAsync(CancellationToken ct)
	{
		var client = new ImapClient();
		try
		{
			await client.ConnectAsync(
				settings.Host,
				settings.Port,
				settings.UseSsl ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.None,
				ct
			);
			await client.AuthenticateAsync(settings.UserName, settings.Password, ct);

			// Re-read on every session: a server can change what it advertises across a
			// version upgrade, and a stale tier silently disables reconciliation.
			capabilities = ImapCapabilityNegotiation.Build(client.Capabilities);
			return client;
		}
		catch
		{
			client.Dispose();
			throw;
		}
	}

	public async Task<AuthResult> AuthenticateAsync(Account account, CancellationToken ct)
	{
		try
		{
			using var client = await ConnectAsync(ct);
			await client.DisconnectAsync(true, ct);
			return new AuthResult(true, AuthState.Connected, null);
		}
		catch (AuthenticationException ex)
		{
			return new AuthResult(
				false,
				AuthState.NeedsReauth,
				Problem(ErrorCategory.Auth, "Authentication failed", ex.Message)
			);
		}
		catch (Exception ex) when (ex is ImapProtocolException or IOException or SocketException)
		{
			return new AuthResult(
				false,
				AuthState.Error,
				Problem(ErrorCategory.Network, "Could not reach the mail server", ex.Message)
			);
		}
	}

	public async Task<IReadOnlyList<MailboxDto>> ListMailboxesAsync(
		Account account,
		CancellationToken ct
	)
	{
		using var client = await ConnectAsync(ct);

		// The delimiter and prefix are server-declared and must never be assumed (§1).
		var space = client.PersonalNamespaces[0];
		var delimiter = space.DirectorySeparator;
		var prefix = string.IsNullOrEmpty(space.Path) ? null : space.Path;

		var result = new List<MailboxDto>();

		foreach (var folder in await client.GetFoldersAsync(space, cancellationToken: ct))
		{
			if (folder.Attributes.HasFlag(FolderAttributes.NonExistent))
			{
				continue;
			}

			await folder.StatusAsync(StatusItems.Count | StatusItems.Unread, ct);

			result.Add(
				new MailboxDto
				{
					// The full folder name is the provider identifier, never the display name
					// or a path we derived (§1).
					ProviderMailboxId = folder.FullName,
					Name = folder.Name,
					ParentProviderMailboxId = string.IsNullOrEmpty(folder.ParentFolder?.FullName)
						? null
						: folder.ParentFolder.FullName,
					SpecialUse = SpecialUseOf(folder),
					IsSubscribed = folder.IsSubscribed,
					TotalCount = folder.Count,
					UnreadCount = folder.Unread,
					ImapMetadata = new ImapMailboxMetadataDto(
						folder.FullName,
						delimiter.ToString(),
						prefix
					),
				}
			);
		}

		await client.DisconnectAsync(true, ct);
		return result;
	}

	private static SpecialUse SpecialUseOf(IMailFolder folder) =>
		folder switch
		{
			_ when folder.Attributes.HasFlag(FolderAttributes.Inbox) => SpecialUse.Inbox,
			_ when folder.Attributes.HasFlag(FolderAttributes.Sent) => SpecialUse.Sent,
			_ when folder.Attributes.HasFlag(FolderAttributes.Drafts) => SpecialUse.Drafts,
			_ when folder.Attributes.HasFlag(FolderAttributes.Trash) => SpecialUse.Trash,
			_ when folder.Attributes.HasFlag(FolderAttributes.Junk) => SpecialUse.Junk,
			_ when folder.Attributes.HasFlag(FolderAttributes.Archive) => SpecialUse.Archive,
			_ => SpecialUse.None,
		};

	public async Task<int> EstimateMailboxCountAsync(
		Account account,
		Mailbox mailbox,
		CancellationToken ct
	)
	{
		using var client = await ConnectAsync(ct);
		var folder = await OpenAsync(client, mailbox, FolderAccess.ReadOnly, ct);
		var count = folder.Count;
		await client.DisconnectAsync(true, ct);
		return count;
	}

	private static async Task<IMailFolder> OpenAsync(
		ImapClient client,
		Mailbox mailbox,
		FolderAccess access,
		CancellationToken ct
	)
	{
		var providerId =
			mailbox.ProviderMailboxId
			?? throw new InvalidOperationException("IMAP mailboxes always have a provider id");

		var folder = await client.GetFolderAsync(providerId, ct);
		await folder.OpenAsync(access, ct);
		return folder;
	}

	private static MutationProblemDetails Problem(
		ErrorCategory category,
		string title,
		string detail,
		string? providerCode = null
	) =>
		new()
		{
			Title = title,
			Detail = detail,
			Category = category,
			ProviderCode = providerCode,
		};

	public Task<AttachmentConstraints> GetAttachmentConstraintsAsync(
		Account account,
		CancellationToken ct
	) =>
		// IMAP advertises no message-size limit, and guessing one would be a false precision
		// the user is then blocked by (§15).
		Task.FromResult(
			new AttachmentConstraints(
				null,
				null,
				account.AttachmentSizeLimitOverride,
				IsUnknown: account.AttachmentSizeLimitOverride is null
			)
		);
}
