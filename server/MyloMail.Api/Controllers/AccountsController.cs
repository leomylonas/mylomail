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
			return Problem(
				$"{request.ProviderType} accounts authenticate interactively and take no password.",
				statusCode: StatusCodes.Status400BadRequest,
				title: "Unexpected credential"
			);
		}

		if (request.ProviderType == ProviderType.Imap && request.Imap is null)
		{
			return Problem(
				"IMAP accounts need host, port and user name.",
				statusCode: StatusCodes.Status400BadRequest,
				title: "Missing IMAP settings"
			);
		}
		if (string.IsNullOrWhiteSpace(request.EmailAddress))
		{
			// `Account` has no address column (§1) — this becomes the default `SendIdentity`'s
			// address, the authoritative one for the account. Nothing downstream re-checks it,
			// so an empty string here would silently create an account with no usable address.
			return Problem(
				"An account needs an email address.",
				statusCode: StatusCodes.Status400BadRequest,
				title: "Missing email address"
			);
		}
		if (request.ProviderType == ProviderType.Imap && request.Imap is { Host: null or "" })
		{
			// AddAccount.tsx's "imapReady" gate keeps a blank host out of the normal flow, but
			// nothing mirrors that here. An empty host reaches MailKit's ImapClient.ConnectAsync,
			// which throws a bare ArgumentException — a type none of AuthenticateAsync's or this
			// controller's catch clauses recognise, so it used to surface as an unhandled 500
			// instead of the clean 400 every other rejected input gets.
			return Problem(
				"IMAP accounts need a host.",
				statusCode: StatusCodes.Status400BadRequest,
				title: "Missing IMAP host"
			);
		}
		if (request.ProviderType == ProviderType.Imap && request.Imap is { SmtpHost: null or "" })
		{
			// Same failure mode as the IMAP host above, on the SMTP connection Send.cs opens.
			return Problem(
				"IMAP accounts need an SMTP host.",
				statusCode: StatusCodes.Status400BadRequest,
				title: "Missing SMTP host"
			);
		}
		if (request.CalDav is { ReuseImapCredential: false, Secret: null or "" })
		{
			return Problem("Independent CalDAV credentials need a password.", statusCode: 400, title: "Missing CalDAV credential");
		}
		if (request.CalDav is not null
			&& (!Uri.TryCreate(request.CalDav.Endpoint, UriKind.Absolute, out var endpoint)
				|| endpoint.Scheme != Uri.UriSchemeHttps))
		{
			return Problem("CalDAV Basic authentication requires an absolute HTTPS endpoint.", statusCode: 400, title: "Invalid CalDAV endpoint");
		}
		if (request.Imap is { ReuseImapCredentialForSmtp: false, SmtpSecret: null or "" })
		{
			return Problem("Independent SMTP credentials need a password.", statusCode: 400, title: "Missing SMTP credential");
		}
		if (request.Imap is { ReuseImapCredentialForSmtp: false, SmtpUserName: null or "" })
		{
			return Problem("Independent SMTP credentials need a user name.", statusCode: 400, title: "Missing SMTP user name");
		}
		if (request.InitialSyncMode != InitialSyncMode.Full && request.InitialSyncBoundValue is not > 0)
		{
			return Problem(
				"A bounded initial sync needs a positive month/message count.",
				statusCode: StatusCodes.Status400BadRequest,
				title: "Missing initial sync bound"
			);
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
					request.InitialSyncBoundValue
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
			return Problem(ex.Message, statusCode: StatusCodes.Status400BadRequest, title: "Authentication failed");
		}
		catch (ProviderNotConfiguredException ex)
		{
			// The user cannot fix this: the deployment is missing a client registration.
			return Problem(ex.Message, statusCode: StatusCodes.Status501NotImplemented, title: "Provider not configured");
		}
		catch (CredentialStoreUnavailableException ex)
		{
			// Unlike every background job (§3), this is a synchronous, user-initiated call with
			// no earlier catch to translate it — left unhandled here, it would surface as a bare
			// 500 with no useful detail, matching neither AddAccount.tsx's genuine failures above
			// nor the friendly "unlock your keychain" story pass 64 built everywhere else.
			return Problem(ex.Message, statusCode: StatusCodes.Status503ServiceUnavailable, title: "Credential store unavailable");
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
			var problem = new ProblemDetails
			{
				Title = "Export in progress",
				Detail = "This account has a bulk export still running. Remove anyway to stop it, or wait for the export to finish first.",
				Status = StatusCodes.Status409Conflict,
			};
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
			return NotFound();
		}
		catch (AccountAuthenticationFailedException ex)
		{
			if (ex.Problem is { } problem)
			{
				problem.Status = StatusCodes.Status400BadRequest;
				return new ObjectResult(problem) { StatusCode = StatusCodes.Status400BadRequest };
			}
			return Problem(ex.Message, statusCode: StatusCodes.Status400BadRequest, title: "Authentication failed");
		}
		catch (CredentialStoreUnavailableException ex)
		{
			// Same gap as Add above: this call retrieves and re-stores the prior secret before
			// authenticating, and a locked keychain there would otherwise surface as a bare 500.
			return Problem(ex.Message, statusCode: StatusCodes.Status503ServiceUnavailable, title: "Credential store unavailable");
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
			account.CertificateTrustMode,
			account.AttachmentSizeLimitOverride,
			(account.ProviderConfig as ImapProviderConfig)?.AppendToSentOnSend,
			gate.Delay(account.Id) > TimeSpan.Zero
		);

	private static ProviderConfig? ToProviderConfig(AddAccountRequest request) =>
		request.Imap is null
			? null
			: new ImapProviderConfig
			{
				Host = request.Imap.Host,
				Port = request.Imap.Port,
				UseSsl = request.Imap.UseSsl,
				UserName = request.Imap.UserName,
				SmtpHost = request.Imap.SmtpHost,
				SmtpPort = request.Imap.SmtpPort,
				SmtpCredentialSource = request.Imap.ReuseImapCredentialForSmtp ? CredentialSource.ReuseImap : CredentialSource.Independent,
				SmtpUserName = request.Imap.SmtpUserName,
				CalDav = request.CalDav is null ? null : new CalDavProviderConfig
				{
					Endpoint = request.CalDav.Endpoint,
					UserName = request.CalDav.UserName,
					CredentialSource = request.CalDav.ReuseImapCredential ? CredentialSource.ReuseImap : CredentialSource.Independent,
				},
			};

	/// <summary>
	/// The IMAP password, as an opaque store payload. Only IMAP has one: Gmail and Graph
	/// obtain their own tokens through an interactive flow and own the cache they store (§4).
	/// </summary>
	private static CredentialPayload? ToSecret(AddAccountRequest request) =>
		string.IsNullOrEmpty(request.Secret)
			? null
			: new CredentialPayload(
				MailProviderFactory.ImapPasswordFormat,
				System.Text.Encoding.UTF8.GetBytes(request.Secret)
			);

	private static CredentialPayload? ToCalDavSecret(AddAccountRequest request) =>
		string.IsNullOrEmpty(request.CalDav?.Secret)
			? null
			: new CredentialPayload("caldav-basic-password", System.Text.Encoding.UTF8.GetBytes(request.CalDav.Secret));

	private static CredentialPayload? ToSmtpSecret(AddAccountRequest request) =>
		string.IsNullOrEmpty(request.Imap?.SmtpSecret)
			? null
			: new CredentialPayload("smtp-basic-password", System.Text.Encoding.UTF8.GetBytes(request.Imap.SmtpSecret));
}
