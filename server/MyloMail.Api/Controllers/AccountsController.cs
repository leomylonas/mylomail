using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MyloMail.Api.Accounts;
using MyloMail.Api.Contracts;
using MyloMail.Api.Credentials;
using MyloMail.Api.Domain;
using MyloMail.Api.Errors;
using MyloMail.Api.Persistence;
using MyloMail.Api.Providers;
using MyloMail.Api.Scheduling;

namespace MyloMail.Api.Controllers;

/// <summary>Account management (Epic 1).</summary>
[ApiController]
[Route("accounts")]
public class AccountsController(
	MyloMailDbContext context,
	AccountProvisioningService provisioning,
	AccountGate gate
) : ControllerBase
{
	/// <summary>
	/// Every enabled and disabled account, with the address from its default identity.
	/// </summary>
	/// <remarks>
	/// Disabled accounts are included because removal sets <c>IsEnabled = false</c> first and
	/// this endpoint is also how a caller observes that transition; the sidebar filters them
	/// out itself (§1).
	/// </remarks>
	[HttpGet]
	public async Task<ActionResult<IReadOnlyList<AccountDto>>> List(CancellationToken ct)
	{
		var accounts = await context
			.Accounts.Select(account => new
			{
				Account = account,
				Address = context
					.SendIdentities.Where(i => i.AccountId == account.Id && i.IsDefault)
					.Select(i => i.EmailAddress)
					.FirstOrDefault(),
			})
			.ToListAsync(ct);

		return Ok(
			accounts
				.OrderBy(row => row.Account.SortOrder)
				.Select(row => ToDto(row.Account, row.Address))
				.ToList()
		);
	}

	[HttpPost]
	public async Task<ActionResult<AccountDto>> Add(AddAccountRequest request, CancellationToken ct)
	{
		if (request.Secret is not null && request.ProviderType != ProviderType.Imap)
		{
			// Gmail and Graph authenticate interactively; there is no password to supply, and
			// accepting one silently would leave the user believing they had configured
			// something.
			return this.MutationProblem($"{request.ProviderType} accounts authenticate interactively and take no password.",
				statusCode: StatusCodes.Status400BadRequest,
				title: "Unexpected credential");
		}
		if (request.ProviderType != ProviderType.Gmail
			&& (!string.IsNullOrWhiteSpace(request.GmailClientId)
				|| !string.IsNullOrWhiteSpace(request.GmailClientSecret)))
		{
			return this.MutationProblem("Google OAuth client credentials can only be used with a Gmail account.",
				statusCode: StatusCodes.Status400BadRequest,
				title: "Unexpected Google OAuth credentials");
		}
		if (request.ProviderType == ProviderType.Gmail
			&& string.IsNullOrWhiteSpace(request.GmailClientId)
				!= string.IsNullOrWhiteSpace(request.GmailClientSecret))
		{
			return this.MutationProblem("A custom Google OAuth registration needs both its client id and client secret.",
				statusCode: StatusCodes.Status400BadRequest,
				title: "Incomplete Google OAuth credentials");
		}

		if (request.ProviderType == ProviderType.Imap && request.Imap is null)
		{
			return this.MutationProblem("IMAP accounts need host, port and user name.",
				statusCode: StatusCodes.Status400BadRequest,
				title: "Missing IMAP settings");
		}
		if (string.IsNullOrWhiteSpace(request.EmailAddress))
		{
			// `Account` has no address column (§1) — this becomes the default `SendIdentity`'s
			// address, the authoritative one for the account. Nothing downstream re-checks it,
			// so an empty string here would silently create an account with no usable address.
			return this.MutationProblem("An account needs an email address.",
				statusCode: StatusCodes.Status400BadRequest,
				title: "Missing email address");
		}
		if (request.ProviderType == ProviderType.Imap && request.Imap is { Host: null or "" })
		{
			return this.MutationProblem("IMAP accounts need a host.",
				statusCode: StatusCodes.Status400BadRequest,
				title: "Missing IMAP host");
		}
		if (request.ProviderType == ProviderType.Imap && request.Imap is { SmtpHost: null or "" })
		{
			return this.MutationProblem("IMAP accounts need an SMTP host.",
				statusCode: StatusCodes.Status400BadRequest,
				title: "Missing SMTP host");
		}
		if (request.Imap is
			{
				ReuseImapCredentialForSmtp: false,
				SmtpAuthMethod: not SmtpAuthMethod.None,
				SmtpSecret: null or "",
			})
		{
			return this.MutationProblem("Independent SMTP credentials need a secret.",
				statusCode: StatusCodes.Status400BadRequest,
				title: "Missing SMTP credential");
		}
		if (request.Imap is
			{
				ReuseImapCredentialForSmtp: false,
				SmtpAuthMethod: not SmtpAuthMethod.None,
				SmtpUserName: null or "",
			})
		{
			return this.MutationProblem("Independent SMTP credentials need a user name.",
				statusCode: StatusCodes.Status400BadRequest,
				title: "Missing SMTP user name");
		}
		if (request.Imap is
			{
				AuthMethod: ImapAuthMethod.Password,
				ImapSecurity: MailTransportSecurity.None,
			})
		{
			return this.MutationProblem("IMAP password authentication requires TLS on connect or mandatory STARTTLS.",
				statusCode: StatusCodes.Status400BadRequest,
				title: "Insecure IMAP authentication");
		}
		if (request.Imap is
			{
				SmtpAuthMethod: not SmtpAuthMethod.None,
				SmtpSecurity: MailTransportSecurity.None,
			})
		{
			return this.MutationProblem("SMTP authentication requires TLS on connect or mandatory STARTTLS.",
				statusCode: StatusCodes.Status400BadRequest,
				title: "Insecure SMTP authentication");
		}
		if (request.Imap is
			{
				ReuseImapCredentialForSmtp: true,
				SmtpAuthMethod: not SmtpAuthMethod.None,
			} imap
			&& (imap.AuthMethod == ImapAuthMethod.OAuth2)
				!= (imap.SmtpAuthMethod == SmtpAuthMethod.OAuth2))
		{
			return this.MutationProblem("Reusing the IMAP credential requires matching password or OAuth 2 authentication methods.",
				statusCode: StatusCodes.Status400BadRequest,
				title: "Incompatible SMTP credential");
		}
		if (request.Imap is { AuthMethod: ImapAuthMethod.OAuth2 }
			&& request.CalDav is { ReuseImapCredential: true })
		{
			return this.MutationProblem("CalDAV Basic authentication cannot reuse an IMAP OAuth 2 token. Supply an independent CalDAV password.",
				statusCode: StatusCodes.Status400BadRequest,
				title: "Incompatible CalDAV credential");
		}
		if (request.CalDav is { ReuseImapCredential: false, Secret: null or "" })
		{
			return this.MutationProblem("Independent CalDAV credentials need a password.",
				statusCode: StatusCodes.Status400BadRequest,
				title: "Missing CalDAV credential");
		}
		if (request.CalDav is not null
			&& (!Uri.TryCreate(request.CalDav.Endpoint, UriKind.Absolute, out var endpoint)
				|| endpoint.Scheme != Uri.UriSchemeHttps))
		{
			return this.MutationProblem("CalDAV Basic authentication requires an absolute HTTPS endpoint.",
				statusCode: StatusCodes.Status400BadRequest,
				title: "Invalid CalDAV endpoint");
		}
		if (request.InitialSyncMode != InitialSyncMode.Full && request.InitialSyncBoundValue is not > 0)
		{
			return this.MutationProblem("A bounded initial sync needs a positive month/message count.",
				statusCode: StatusCodes.Status400BadRequest,
				title: "Missing initial sync bound");
		}

		try
		{
			var account = await provisioning.AddAsync(
				new NewAccount(
					request.DisplayName,
					request.ProviderType,
					request.EmailAddress,
					ToProviderConfig(request),
					ToSecret(request),
					ToCalDavSecret(request),
					ToSmtpSecret(request),
					request.CertificateTrustMode,
					request.InitialSyncMode,
					request.InitialSyncBoundValue,
					ToGmailClientSecret(request)
				),
				ct
			);

			return CreatedAtAction(nameof(List), ToDto(account, request.EmailAddress));
		}
		catch (AccountAuthenticationFailedException ex)
		{
			// The user can fix this by correcting what they typed — or, when the rejection was
			// a certificate MyloMail doesn't trust, by retrying with CertificateTrustMode set.
			// The fingerprint/hostname a certificate rejection carries live in the problem's own
			// Extensions rather than being lost to ex.Message's plain text.
			if (ex.Problem is { } problem)
			{
				problem.Status = StatusCodes.Status400BadRequest;
				return new ObjectResult(problem) { StatusCode = StatusCodes.Status400BadRequest };
			}
			return this.MutationProblem(ex.Message, statusCode: StatusCodes.Status400BadRequest, title: "Authentication failed", category: ErrorCategory.Auth);
		}
		catch (ProviderNotConfiguredException ex)
		{
			// The user cannot fix this: the deployment is missing a client registration.
			return this.MutationProblem(ex.Message, statusCode: StatusCodes.Status501NotImplemented, title: "Provider not configured", category: ErrorCategory.Unknown);
		}
		catch (CredentialStoreUnavailableException ex)
		{
			// Unlike every background job (§3), this is a synchronous, user-initiated call with
			// no earlier catch to translate it — left unhandled here, it would surface as a bare
			// 500 with no useful detail, matching neither AddAccount.tsx's genuine failures above
			// nor the friendly "unlock your keychain" story pass 64 built everywhere else.
			return this.MutationProblem(ex.Message, statusCode: StatusCodes.Status503ServiceUnavailable, title: "Credential store unavailable", category: ErrorCategory.Unknown);
		}
	}

	/// <summary>
	/// Removes an account. Disabling and deletion happen inside the service so that jobs stop
	/// cleanly first (§3).
	/// </summary>
	/// <param name="force">
	/// Pass <c>true</c> once the user has confirmed removal despite a running export
	/// (<see cref="ExportInProgressException"/>) — the first call without it is how the
	/// renderer discovers that export exists at all.
	/// </param>
	[HttpDelete("{accountId:guid}")]
	public async Task<IActionResult> Remove(Guid accountId, [FromQuery] bool force, CancellationToken ct)
	{
		try
		{
			await provisioning.RemoveAsync(accountId, force, ct);
			return NoContent();
		}
		catch (ExportInProgressException ex)
		{
			var problem = MutationProblemTransport.Create(
				ErrorCategory.Conflict,
				"This account has a bulk export still running. Remove anyway to stop it, or wait for the export to finish first.",
				StatusCodes.Status409Conflict,
				"Export in progress"
			);
			problem.Extensions["exportId"] = ex.ExportId;
			return new ObjectResult(problem) { StatusCode = StatusCodes.Status409Conflict };
		}
	}

	/// <summary>
	/// Re-verifies an account stuck in <see cref="AuthState.NeedsReauth"/> or
	/// <see cref="AuthState.Error"/> and, on success, immediately requeues its blocked work
	/// (§3). A password-changed rejection and a certificate-untrusted rejection surface
	/// identically to <see cref="Add"/>'s own failure response, extensions included, so the
	/// renderer can offer the same "trust this certificate" prompt either place.
	/// </summary>
	[HttpPost("{accountId:guid}/reauthenticate")]
	public async Task<IActionResult> Reauthenticate(
		Guid accountId,
		ReauthenticateAccountRequest request,
		CancellationToken ct
	)
	{
		try
		{
			await provisioning.ReauthenticateAsync(accountId, request.Secret, ct);
			return NoContent();
		}
		catch (KeyNotFoundException)
		{
			return this.MutationProblem(statusCode: StatusCodes.Status404NotFound);
		}
		catch (AccountAuthenticationFailedException ex)
		{
			if (ex.Problem is { } problem)
			{
				problem.Status = StatusCodes.Status400BadRequest;
				return new ObjectResult(problem) { StatusCode = StatusCodes.Status400BadRequest };
			}
			return this.MutationProblem(ex.Message, statusCode: StatusCodes.Status400BadRequest, title: "Authentication failed", category: ErrorCategory.Auth);
		}
		catch (CredentialStoreUnavailableException ex)
		{
			// Same gap as Add above: this call retrieves and re-stores the prior secret before
			// authenticating, and a locked keychain there would otherwise surface as a bare 500.
			return this.MutationProblem(ex.Message, statusCode: StatusCodes.Status503ServiceUnavailable, title: "Credential store unavailable", category: ErrorCategory.Unknown);
		}
	}

	private AccountDto ToDto(Account account, string? address) =>
		new(
			account.Id,
			account.DisplayName,
			address ?? string.Empty,
			account.ProviderType,
			account.AuthState,
			account.IsEnabled,
			account.LastAuthError,
			account.Color,
			account.SortOrder,
			account.SidebarCollapsed,
			account.PollIntervalSeconds,
			account.PollingEnabled,
			account.UndoSendDelaySeconds,
			account.NotificationsEnabled,
			account.InitialSyncMode,
			account.InitialSyncBoundValue,
			account.CertificateTrustMode,
			account.AttachmentSizeLimitOverride,
			(account.ProviderConfig as ImapProviderConfig)?.AppendToSentOnSend,
			gate.Delay(account.Id) > TimeSpan.Zero
		);

	private static ProviderConfig? ToProviderConfig(AddAccountRequest request) =>
		request.ProviderType switch
		{
			ProviderType.Gmail => new GmailProviderConfig
			{
				ClientId = string.IsNullOrWhiteSpace(request.GmailClientId)
					? null
					: request.GmailClientId.Trim(),
			},
			ProviderType.Imap when request.Imap is not null => new ImapProviderConfig
			{
				Host = request.Imap.Host,
				Port = request.Imap.Port,
				ImapSecurity = request.Imap.ImapSecurity,
				AuthMethod = request.Imap.AuthMethod,
				UserName = request.Imap.UserName,
				SmtpHost = request.Imap.SmtpHost,
				SmtpPort = request.Imap.SmtpPort,
				SmtpSecurity = request.Imap.SmtpSecurity,
				SmtpAuthMethod = request.Imap.SmtpAuthMethod,
				SmtpCredentialSource = request.Imap.ReuseImapCredentialForSmtp ? CredentialSource.ReuseImap : CredentialSource.Independent,
				SmtpUserName = request.Imap.SmtpUserName,
				CalDav = request.CalDav is null ? null : new CalDavProviderConfig
				{
					Endpoint = request.CalDav.Endpoint,
					UserName = request.CalDav.UserName,
					CredentialSource = request.CalDav.ReuseImapCredential ? CredentialSource.ReuseImap : CredentialSource.Independent,
				},
			},
			_ => null,
		};

	/// <summary>
	/// The IMAP password, as an opaque store payload. Only IMAP has one: Gmail and Graph
	/// obtain their own tokens through an interactive flow and own the cache they store (§4).
	/// </summary>
	private static CredentialPayload? ToSecret(AddAccountRequest request) =>
		string.IsNullOrEmpty(request.Secret)
			? null
			: new CredentialPayload(
				request.Imap?.AuthMethod == ImapAuthMethod.OAuth2
					? MailProviderFactory.ImapOAuth2TokenFormat
					: MailProviderFactory.ImapPasswordFormat,
				System.Text.Encoding.UTF8.GetBytes(request.Secret)
			);

	private static CredentialPayload? ToCalDavSecret(AddAccountRequest request) =>
		string.IsNullOrEmpty(request.CalDav?.Secret)
			? null
			: new CredentialPayload("caldav-basic-password", System.Text.Encoding.UTF8.GetBytes(request.CalDav.Secret));

	private static CredentialPayload? ToSmtpSecret(AddAccountRequest request) =>
		string.IsNullOrEmpty(request.Imap?.SmtpSecret)
			? null
			: new CredentialPayload(
				request.Imap.SmtpAuthMethod == SmtpAuthMethod.OAuth2
					? MailProviderFactory.SmtpOAuth2TokenFormat
					: MailProviderFactory.SmtpPasswordFormat,
				System.Text.Encoding.UTF8.GetBytes(request.Imap.SmtpSecret)
			);

	private static CredentialPayload? ToGmailClientSecret(AddAccountRequest request) =>
		string.IsNullOrWhiteSpace(request.GmailClientSecret)
			? null
			: new CredentialPayload(
				GmailClientRegistration.SecretFormat,
				System.Text.Encoding.UTF8.GetBytes(request.GmailClientSecret)
			);
}
