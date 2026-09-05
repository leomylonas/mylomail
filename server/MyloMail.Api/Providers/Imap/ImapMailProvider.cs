using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MailKit.Security;
using MimeKit;
using MyloMail.Api.Domain;
using MyloMail.Api.Errors;
using MyloMail.Api.Providers.Contracts;
using MyloMail.Api.Security;

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
	private readonly IProviderMailboxResolver mailboxes;
	private ProviderCapabilities capabilities = ImapCapabilityNegotiation.Unknown;

	/// <summary>
	/// What the certificate-validation callback last rejected, if it rejected anything — the
	/// callback itself can only return a bool, so this is how <see cref="AuthenticateAsync"/>
	/// tells "the certificate was untrusted" apart from every other reason a TLS handshake can
	/// fail, to categorise the resulting <see cref="SslHandshakeException"/> correctly (§15).
	/// </summary>
	private (string Fingerprint, string Issuer)? rejectedCertificate;

	public ImapMailProvider(ImapConnectionSettings settings, IProviderMailboxResolver mailboxes)
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
		rejectedCertificate = null;
		client.ServerCertificateValidationCallback = (_, certificate, _, sslPolicyErrors) =>
		{
			if (certificate is null)
			{
				return false;
			}

			var certificate2 = certificate as X509Certificate2 ?? new X509Certificate2(certificate);
			var trusted = CertificateTrust.Validate(
				settings.CertificateTrustMode,
				settings.TrustedCertificates ?? [],
				settings.Host,
				certificate2,
				sslPolicyErrors
			);
			if (!trusted)
			{
				rejectedCertificate = (CertificateTrust.Fingerprint(certificate2), certificate2.Issuer);
			}
			return trusted;
		};
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
		// Translated here, not left for every one of ConnectAsync's callers to notice: only
		// AuthenticateAsync and Send.cs's own SMTP connect used to catch this specifically —
		// every other caller (mailboxes, sync, mutations, drafts) let a rejected certificate
		// propagate as a raw MailKit SslHandshakeException with no fingerprint/issuer detail,
		// caught (if at all) by a generic handler with no live AuthState signal (§7, §15).
		catch (SslHandshakeException) when (rejectedCertificate is { } rejected)
		{
			client.Dispose();
			throw new ProviderAuthenticationException(
				CertificateTrust.Problem(settings.Host, rejected.Fingerprint, rejected.Issuer).Detail!
			);
		}
		// Same reasoning, for rejected credentials rather than a rejected certificate: a
		// password that stops working mid-session (changed on the server, a revoked app
		// password) previously propagated as a raw MailKit AuthenticationException past every
		// sync caller — none of which catch it — all the way to SyncJobs.GuardAsync, whose
		// generic fallback just stops the poll loop silently, never setting
		// AuthState.NeedsReauth or announcing anything live (§3, §7). Translating here means
		// every caller gets the same NeedsReauth handling AuthenticateAsync's own explicit
		// catch already gave the initial account-setup path.
		catch (AuthenticationException ex)
		{
			client.Dispose();
			throw new ProviderAuthenticationException(ex.Message);
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
		catch (ProviderAuthenticationException) when (rejectedCertificate is { } rejected)
		{
			// ConnectAsync already translated the rejection into this same exception type;
			// rejectedCertificate is still set (it's cleared only at the top of the next
			// ConnectAsync call), so the original structured Problem — fingerprint, issuer,
			// hostname as extension fields, not just the flattened message text — is
			// reconstructed exactly as it was before that translation moved here.
			return new AuthResult(
				false,
				AuthState.Error,
				CertificateTrust.Problem(settings.Host, rejected.Fingerprint, rejected.Issuer)
			);
		}
		catch (ProviderAuthenticationException ex)
		{
			// ConnectAsync now translates a rejected credential the same way it translates a
			// rejected certificate; this is the plain case (rejectedCertificate unset).
			return new AuthResult(
				false,
				AuthState.NeedsReauth,
				Problem(ErrorCategory.Auth, "Authentication failed", ex.Message)
			);
		}
		catch (Exception ex)
			when (ex is ImapProtocolException or IOException or SocketException or SslHandshakeException)
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

		// INBOX explicitly, and first.
		//
		// It does not necessarily appear in the personal namespace's own listing: on a server
		// whose namespace prefix is "INBOX." — which the CondStore tier of the local matrix
		// uses precisely to catch this — enumerating that namespace returns the folders
		// *under* INBOX and not INBOX itself. Enumerating the namespace alone therefore
		// discovers every folder except the one the user cares about most, and the account
		// syncs perfectly while appearing to have no inbox at all.
		var folders = new List<IMailFolder> { client.Inbox };
		folders.AddRange(
			(await client.GetFoldersAsync(space, cancellationToken: ct)).Where(candidate =>
				!candidate.FullName.Equals(client.Inbox.FullName, StringComparison.OrdinalIgnoreCase)
			)
		);

		foreach (var folder in folders)
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
			// Only reached when the server advertised none of RFC 6154's SPECIAL-USE
			// attributes for this folder — never overrides a real one. A server that simply
			// doesn't support the extension would otherwise report every folder as None,
			// silently breaking anything that depends on finding Sent/Trash/Drafts (send
			// reconciliation among them). A user whose server names things this heuristic
			// doesn't recognise (or guesses wrong) can still correct it explicitly via
			// Mailbox.SpecialUseOverride (§13 Epic 2).
			_ => SpecialUseFromName(folder.Name),
		};

	/// <summary>
	/// A modest, English-centric name heuristic — not exhaustive i18n, which is out of scope
	/// for a fallback whose whole purpose is "better than nothing," not "as good as the
	/// server telling us."
	/// </summary>
	internal static SpecialUse SpecialUseFromName(string name) =>
		name.Trim().ToUpperInvariant() switch
		{
			"SENT" or "SENT ITEMS" or "SENT MAIL" => SpecialUse.Sent,
			"TRASH" or "DELETED ITEMS" or "DELETED MESSAGES" or "BIN" => SpecialUse.Trash,
			"DRAFTS" => SpecialUse.Drafts,
			"ARCHIVE" or "ALL MAIL" => SpecialUse.Archive,
			"JUNK" or "JUNK E-MAIL" or "SPAM" => SpecialUse.Junk,
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
