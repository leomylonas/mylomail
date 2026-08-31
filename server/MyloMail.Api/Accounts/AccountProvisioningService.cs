using Microsoft.EntityFrameworkCore;
using MyloMail.Api.Contracts;
using MyloMail.Api.Credentials;
using MyloMail.Api.Domain;
using MyloMail.Api.Hubs;
using MyloMail.Api.Persistence;
using MyloMail.Api.Providers;
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
	CredentialPayload? Secret
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

		if (request.Secret is CredentialPayload secret)
		{
			await credentials.StoreAsync(accountId, secret, ct);
		}

		var account = new Account
		{
			Id = accountId,
			DisplayName = request.DisplayName,
			ProviderType = request.ProviderType,
			ProviderConfig = request.ProviderConfig,
			AuthState = AuthState.Connected,
			IsEnabled = true,
			PollIntervalSeconds = 60,
			InitialSyncMode = InitialSyncMode.Full,
			SortOrder = await context.Accounts.CountAsync(ct),
		};

		try
		{
			var result = await providers.For(account).AuthenticateAsync(account, ct);
			if (!result.Succeeded)
			{
				throw new AccountAuthenticationFailedException(result.Problem?.Detail ?? "Authentication was rejected.");
			}
		}
		catch
		{
			// Nothing has been committed, so the only trace to undo is the credential.
			await credentials.DeleteAsync(accountId, ct);
			throw;
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
			await transaction.CommitAsync(ct);
		});

		logger.LogInformation("Account {AccountId} added for {Provider}.", accountId, request.ProviderType);

		// Announced before scheduling, so a window that is already open shows the account
		// while its first sync is still running rather than only once something else happens
		// to refresh it (§7).
		await events.AccountStatusChangedAsync(AccountDtoFactory.ToDto(account, request.EmailAddress));

		await scheduler.ResumeAccountAsync(accountId, ct);
		return account;
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
	public async Task RemoveAsync(Guid accountId, CancellationToken ct = default)
	{
		var account = await context.Accounts.FirstOrDefaultAsync(a => a.Id == accountId, ct);
		if (account is null)
		{
			return;
		}

		account.IsEnabled = false;
		account.LastAuthError = null;
		await context.SaveChangesAsync(ct);

		context.Accounts.Remove(account);
		await context.SaveChangesAsync(ct);

		// Last, so a crash mid-removal leaves a disabled account with its credential rather
		// than a live account with none.
		await credentials.DeleteAsync(accountId, ct);

		logger.LogInformation("Account {AccountId} removed at {At}.", accountId, clock.GetUtcNow());

		account.IsEnabled = false;
		await events.AccountStatusChangedAsync(AccountDtoFactory.ToDto(account, address: null));
	}
}

internal static class AccountDtoFactory
{
	public static AccountDto ToDto(Account account, string? address) =>
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
}

/// <summary>
/// The provider rejected the supplied credentials while adding an account.
/// </summary>
/// <remarks>
/// Distinct from <see cref="ProviderNotConfiguredException"/>: here the attempt was made and
/// refused, which is something the user can fix by correcting what they typed.
/// </remarks>
public sealed class AccountAuthenticationFailedException(string message) : Exception(message);
