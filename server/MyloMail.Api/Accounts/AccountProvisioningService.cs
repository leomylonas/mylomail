using Microsoft.EntityFrameworkCore;
using MyloMail.Api.Content;
using MyloMail.Api.Contracts;
using MyloMail.Api.Credentials;
using MyloMail.Api.Domain;
using MyloMail.Api.Hubs;
using MyloMail.Api.Persistence;
using MyloMail.Api.Providers;
using MyloMail.Api.Providers.Contracts;
using MyloMail.Api.Scheduling;

namespace MyloMail.Api.Accounts;

/// <summary>What the caller supplies to add an account. Never a stored credential.</summary>
/// <remarks>
/// <paramref name="Secret"/> is the password or token payload handed straight to the
/// credential store and never written to a column (§4). It is a parameter rather than a field
/// on <see cref="Account"/> precisely so there is no shape in which it could be persisted by
/// accident.
/// </remarks>
public sealed record NewAccount(
	string DisplayName,
	ProviderType ProviderType,
	string EmailAddress,
	ProviderConfig? ProviderConfig,
	CredentialPayload? Secret,
	CredentialPayload? CalDavSecret = null,
	CredentialPayload? SmtpSecret = null,
	CertificateTrustMode CertificateTrustMode = CertificateTrustMode.Default,
	InitialSyncMode InitialSyncMode = InitialSyncMode.Full,
	int? InitialSyncBoundValue = null,
	CredentialPayload? GmailClientSecret = null
);

/// <summary>
/// Creates an account, its authoritative send identity and its credential, and starts it
/// syncing (§1, §4).
/// </summary>
public sealed class AccountProvisioningService(
	MyloMailDbContext context,
	ICredentialStore credentials,
	IMailProviderFactory providers,
	StartupScheduler scheduler,
	IHubEvents events,
	SearchIndexer search,
	TimeProvider clock,
	ILogger<AccountProvisioningService> logger
)
{
	/// <summary>
	/// Verifies the credentials before persisting anything the user would then have to
	/// remove.
	/// </summary>
	/// <remarks>
	/// <para>
	/// The order matters and is awkward on purpose. The credential store is keyed by account
	/// id, and the provider reads the credential at construction, so the id is minted and the
	/// secret stored <i>before</i> the account row exists. If authentication then fails, the
	/// stored secret is deleted again — otherwise a rejected attempt would leave an orphaned
	/// credential under an id nothing references, which no later code would ever clean up.
	/// </para>
	/// <para>
	/// Nothing is scheduled until the row is committed: a sync job that outran its own account
	/// would find nothing and quietly do nothing.
	/// </para>
	/// </remarks>
	public async Task<Account> AddAsync(NewAccount request, CancellationToken ct = default)
	{
		var accountId = Guid.NewGuid();

		var account = new Account
		{
			Id = accountId,
			DisplayName = request.DisplayName,
			ProviderType = request.ProviderType,
			ProviderConfig = request.ProviderConfig,
			AuthState = AuthState.Connected,
			IsEnabled = true,
			PollIntervalSeconds = 60,
			InitialSyncMode = request.InitialSyncMode,
			InitialSyncBoundValue = request.InitialSyncMode == InitialSyncMode.Full
				? null
				: request.InitialSyncBoundValue,
			SortOrder = await context.Accounts.CountAsync(ct),
			// Never notify for anything dated before this instant (§13 Epic 9) — set once,
			// here, and never moved afterwards.
			NotificationEpoch = clock.GetUtcNow(),
			CertificateTrustMode = request.CertificateTrustMode,
		};

		var commitAttempted = false;
		try
		{
			if (request.Secret is CredentialPayload secret)
			{
				await credentials.StoreAsync(accountId, secret, ct);
			}
			if (request.CalDavSecret is CredentialPayload calDavSecret)
			{
				await credentials.StoreSlotAsync(accountId, CredentialSlots.CalDav, calDavSecret, ct);
			}
			if (request.SmtpSecret is CredentialPayload smtpSecret)
			{
				await credentials.StoreSlotAsync(accountId, CredentialSlots.Smtp, smtpSecret, ct);
			}
			if (request.GmailClientSecret is CredentialPayload gmailClientSecret)
			{
				await credentials.StoreSlotAsync(
					accountId,
					CredentialSlots.GmailClientSecret,
					gmailClientSecret,
					ct
				);
			}

			var result = await providers.For(account).AuthenticateAsync(account, ct);
			if (!result.Succeeded)
			{
				throw new AccountAuthenticationFailedException(result.Problem);
			}
			var strategy = context.Database.CreateExecutionStrategy();
			await strategy.ExecuteAsync(async () =>
			{
				await using var transaction = await context.Database.BeginTransactionAsync(ct);

				context.Accounts.Add(account);

				// The default identity's address is the authoritative address for the account —
				// Account deliberately has no address column, to avoid two sources of truth (§1).
				context.SendIdentities.Add(
					new SendIdentity
					{
						Id = Guid.NewGuid(),
						AccountId = accountId,
						DisplayName = request.DisplayName,
						EmailAddress = request.EmailAddress,
						IsDefault = true,
					}
				);

				await context.SaveChangesAsync(ct);
				commitAttempted = true;
				await transaction.CommitAsync(ct);
			});
		}
		catch
		{
			if (!commitAttempted)
			{
				// The account cannot exist yet, so the credential slots are orphaned.
				await credentials.DeleteAsync(accountId, ct);
				await credentials.DeleteSlotAsync(accountId, CredentialSlots.CalDav, ct);
				await credentials.DeleteSlotAsync(accountId, CredentialSlots.Smtp, ct);
				await credentials.DeleteSlotAsync(accountId, CredentialSlots.GmailClientSecret, ct);
			}
			else
			{
				// A commit exception is ambiguous: it may have reached SQLite. Retaining the
				// credentials is deliberate; a live account with no credential is worse than an
				// unreferenced credential, and absence of a local success record proves nothing.
			}
			throw;
		}

		logger.LogInformation("Account {AccountId} added for {Provider}.", accountId, request.ProviderType);

		// Announced before scheduling, so a window that is already open shows the account
		// while its first sync is still running rather than only once something else happens
		// to refresh it (§7).
		await events.AccountStatusChangedAsync(AccountDtoFactory.ToDto(account, request.EmailAddress));

		await scheduler.ResumeAccountAsync(accountId, ct);
		return account;
	}

	/// <summary>
	/// Re-verifies an account that fell into <see cref="AuthState.NeedsReauth"/> or
	/// <see cref="AuthState.Error"/> — a changed password, or a server whose certificate
	/// rotated and whose new fingerprint the user has since pinned via <c>TrustCertificate</c>.
	/// </summary>
	/// <remarks>
	/// <para>
	/// Unlike <see cref="AddAsync"/>, the account already exists, so there is nothing to roll
	/// back on failure — a rejected attempt just leaves the account exactly as paused as it
	/// already was, with an up-to-date <see cref="MutationProblemDetails"/> for the caller to
	/// show (the same certificate-untrusted prompt <c>AddAccount</c> uses, since pinning a
	/// specific fingerprint is finally possible here — the account id it needs now exists).
	/// </para>
	/// <para>
	/// Except the credential: a rejection for a reason unrelated to the password — a transient
	/// network failure, or a certificate the user has not pinned yet — must not cost the
	/// account its last known-working one. The prior payload is read back before a new secret
	/// overwrites it and restored if this attempt fails, so a still-correct password survives
	/// an unrelated rejection rather than being silently destroyed by it.
	/// </para>
	/// </remarks>
	public async Task ReauthenticateAsync(Guid accountId, string? secret, CancellationToken ct = default)
	{
		var account =
			await context.Accounts.FirstOrDefaultAsync(a => a.Id == accountId, ct)
			?? throw new KeyNotFoundException($"No account {accountId}.");

		CredentialPayload? priorSecret = null;
		if (secret is { Length: > 0 })
		{
			if (account.ProviderType != ProviderType.Imap)
			{
				throw new AccountAuthenticationFailedException(
					new Errors.MutationProblemDetails
					{
						Title = "Unexpected credential",
						Detail = $"{account.ProviderType} accounts authenticate interactively and take no password.",
						Category = Errors.ErrorCategory.Validation,
					}
				);
			}
			priorSecret = await credentials.RetrieveAsync(accountId, ct);
			var credentialFormat = account.ProviderConfig is ImapProviderConfig
			{
				AuthMethod: ImapAuthMethod.OAuth2,
			}
				? MailProviderFactory.ImapOAuth2TokenFormat
				: MailProviderFactory.ImapPasswordFormat;
			await credentials.StoreAsync(
				accountId,
				new CredentialPayload(credentialFormat, System.Text.Encoding.UTF8.GetBytes(secret)),
				ct
			);
		}

		AuthResult result;
		try
		{
			result = await providers.For(account).AuthenticateAsync(account, ct);
		}
		catch
		{
			if (priorSecret is not null)
			{
				await credentials.StoreAsync(accountId, priorSecret, ct);
			}
			throw;
		}
		if (!result.Succeeded)
		{
			if (priorSecret is not null)
			{
				await credentials.StoreAsync(accountId, priorSecret, ct);
			}

			account.LastAuthError = result.Problem?.Detail;
			await context.SaveChangesAsync(ct);
			throw new AccountAuthenticationFailedException(result.Problem);
		}

		await scheduler.ResumeAccountAsync(accountId, ct);
	}

	/// <summary>
	/// Soft-disables an account so its jobs stop cleanly, then removes it.
	/// </summary>
	/// <remarks>
	/// <c>IsEnabled</c> is cleared and committed first, and jobs check it at entry and before
	/// enqueueing a successor, so removal does not have to prove no worker is running (§3).
	/// It does not make every job harmless: a provider call already in flight may still
	/// complete remotely.
	/// </remarks>
	/// <param name="force">
	/// A running <see cref="ExportJob"/> writes local `.eml` files for the account being
	/// removed — nothing about that is remote or reconciliation-relevant, but the user should
	/// still get a say rather than losing an in-progress export silently. When an export is
	/// still <see cref="ExportJobStatus.Running"/> or <see cref="ExportJobStatus.CancelRequested"/>
	/// and <paramref name="force"/> is <c>false</c>, this throws <see cref="ExportInProgressException"/>
	/// instead of removing anything, so the caller can ask first.
	/// </param>
	public async Task RemoveAsync(Guid accountId, bool force = false, CancellationToken ct = default)
	{
		var account = await context.Accounts.FirstOrDefaultAsync(a => a.Id == accountId, ct);
		if (account is null)
		{
			return;
		}

		// SQLite can't translate ORDER BY over DateTimeOffset (ExportJob.CreatedAt), so this
		// picks whichever active export happens to sort first locally rather than the most
		// recent — fine for surfacing "an export is running", and the force branch below acts
		// on every active export for the account, not just this one, so nothing is missed if
		// StartAsync's caller ever allows more than one concurrently.
		var activeExports = await context
			.ExportJobs.Where(j =>
				j.AccountId == accountId
				&& (j.Status == ExportJobStatus.Running || j.Status == ExportJobStatus.CancelRequested)
			)
			.ToListAsync(ct);
		if (activeExports.Count > 0 && !force)
		{
			throw new ExportInProgressException(activeExports[0].Id);
		}

		account.IsEnabled = false;
		account.LastAuthError = null;
		await context.SaveChangesAsync(ct);

		if (activeExports.Count > 0)
		{
			// The user chose to remove anyway: stop every active export explicitly rather than
			// let it race the deletion below and fail with a raw exception once the account is
			// gone.
			await context
				.ExportJobs.Where(j => j.AccountId == accountId && j.Status == ExportJobStatus.Running)
				.ExecuteUpdateAsync(u => u.SetProperty(j => j.Status, ExportJobStatus.CancelRequested), ct);
		}

		// Before the account goes: messages cascade from it, and a message cannot be deleted
		// while it is still indexed — the index would otherwise be left describing rows that
		// no longer exist (§8).
		await search.RemoveForAccountAsync(accountId, ct);

		// Mailbox.ParentId is Restrict, not Cascade (a topology sync must never let one
		// deleted folder silently take its whole subtree with it) — but that same guard blocks
		// SQLite's own FK-cascade from Account down to Mailbox here: it deletes a parent
		// mailbox row while a child mailbox still references it, which SQLite refuses with a
		// FOREIGN KEY constraint failure. Every mailbox in this account is about to be deleted
		// together via that same cascade, so breaking every parent link first is safe — there
		// is no longer a subtree left for Restrict to protect.
		await context
			.Mailboxes.Where(m => m.AccountId == accountId)
			.ExecuteUpdateAsync(u => u.SetProperty(m => m.ParentId, (Guid?)null), ct);

		context.Accounts.Remove(account);
		await context.SaveChangesAsync(ct);

		// Last, so a crash mid-removal leaves a disabled account with its credential rather
		// than a live account with none.
		await credentials.DeleteAsync(accountId, ct);
		await credentials.DeleteSlotAsync(accountId, CredentialSlots.CalDav, ct);
		await credentials.DeleteSlotAsync(accountId, CredentialSlots.Smtp, ct);
		await credentials.DeleteSlotAsync(accountId, CredentialSlots.GmailClientSecret, ct);

		logger.LogInformation("Account {AccountId} removed at {At}.", accountId, clock.GetUtcNow());

		account.IsEnabled = false;
		await events.AccountStatusChangedAsync(AccountDtoFactory.ToDto(account, address: null));
	}
}

internal static class AccountDtoFactory
{
	public static AccountDto ToDto(Account account, string? address, Scheduling.AccountGate? gate = null) =>
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
			gate is not null && gate.Delay(account.Id) > TimeSpan.Zero
		);

	/// <summary>
	/// Announces an <see cref="Account.AuthState"/> transition the same way
	/// <see cref="AccountProvisioningService"/>'s own transitions do (§7), for the background
	/// jobs that also flip it — entering <see cref="AuthState.NeedsReauth"/> on an auth failure,
	/// or <see cref="StartupScheduler.ResumeAccountAsync"/> clearing it back to
	/// <see cref="AuthState.Connected"/>. Looks up the default send identity's address itself so
	/// every call site doesn't have to, mirroring <c>AccountsController.List</c>'s query.
	/// </summary>
	public static async Task AnnounceStatusAsync(
		MyloMailDbContext context,
		Hubs.IHubEvents events,
		Account account,
		CancellationToken ct,
		Scheduling.AccountGate? gate = null
	)
	{
		var address = await context
			.SendIdentities.Where(i => i.AccountId == account.Id && i.IsDefault)
			.Select(i => i.EmailAddress)
			.FirstOrDefaultAsync(ct);
		await events.AccountStatusChangedAsync(ToDto(account, address, gate));
	}
}

/// <summary>
/// The provider rejected the supplied credentials while adding an account.
/// </summary>
/// <remarks>
/// Distinct from <see cref="ProviderNotConfiguredException"/>: here the attempt was made and
/// refused, which is something the user can fix by correcting what they typed.
/// <para>
/// Carries the full <see cref="Errors.MutationProblemDetails"/>, not just its text — a
/// certificate-untrusted rejection's fingerprint and hostname live in
/// <see cref="Errors.MutationProblemDetails.Extensions"/>, and the controller needs those to
/// answer with more than prose, so <c>TrustCertificate</c> has something to call with.
/// </para>
/// </remarks>
public sealed class AccountAuthenticationFailedException(Errors.MutationProblemDetails? problem)
	: Exception(problem?.Detail ?? "Authentication was rejected.")
{
	public Errors.MutationProblemDetails? Problem { get; } = problem;
}

/// <summary>
/// Thrown by <see cref="AccountProvisioningService.RemoveAsync"/> when a bulk export is still
/// running for the account and the caller did not ask to remove anyway.
/// </summary>
public sealed class ExportInProgressException(Guid exportId)
	: Exception("This account has a bulk export still in progress.")
{
	public Guid ExportId { get; } = exportId;
}
