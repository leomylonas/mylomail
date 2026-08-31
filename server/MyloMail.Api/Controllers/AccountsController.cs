using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MyloMail.Api.Accounts;
using MyloMail.Api.Contracts;
using MyloMail.Api.Credentials;
using MyloMail.Api.Domain;
using MyloMail.Api.Errors;
using MyloMail.Api.Persistence;
using MyloMail.Api.Providers;

namespace MyloMail.Api.Controllers;

/// <summary>Account management (Epic 1).</summary>
[ApiController]
[Route("accounts")]
public class AccountsController(
	MyloMailDbContext context,
	AccountProvisioningService provisioning
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
		if (request.CalDav is { ReuseImapCredential: false, Secret: null or "" })
		{
			return Problem("Independent CalDAV credentials need a password.", statusCode: 400, title: "Missing CalDAV credential");
		}
		if (request.Imap is { ReuseImapCredentialForSmtp: false, SmtpSecret: null or "" })
		{
			return Problem("Independent SMTP credentials need a password.", statusCode: 400, title: "Missing SMTP credential");
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
					ToSmtpSecret(request)
				),
				ct
			);

			return CreatedAtAction(nameof(List), ToDto(account, request.EmailAddress));
		}
		catch (AccountAuthenticationFailedException ex)
		{
			// The user can fix this by correcting what they typed.
			return Problem(ex.Message, statusCode: StatusCodes.Status400BadRequest, title: "Authentication failed");
		}
		catch (ProviderNotConfiguredException ex)
		{
			// The user cannot fix this: the deployment is missing a client registration.
			return Problem(ex.Message, statusCode: StatusCodes.Status501NotImplemented, title: "Provider not configured");
		}
	}

	/// <summary>
	/// Removes an account. Disabling and deletion happen inside the service so that jobs stop
	/// cleanly first (§3).
	/// </summary>
	[HttpDelete("{accountId:guid}")]
	public async Task<IActionResult> Remove(Guid accountId, CancellationToken ct)
	{
		await provisioning.RemoveAsync(accountId, ct);
		return NoContent();
	}

	private static AccountDto ToDto(Account account, string? address) =>
		new(
			account.Id,
			account.DisplayName,
			address ?? string.Empty,
			account.ProviderType,
			account.AuthState,
			account.IsEnabled,
			account.LastAuthError,
			account.Color,
			account.SortOrder
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
