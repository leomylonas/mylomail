using Microsoft.EntityFrameworkCore;
using MyloMail.Api.Content;
using MyloMail.Api.Persistence;

namespace MyloMail.Api.Credentials;

/// <summary>
/// Completes a durable account-removal intent. Database/search deletion precedes
/// idempotent external credential erasure, so every crash point is restartable.
/// </summary>
public sealed class AccountCredentialCleanupService(
	MyloMailDbContext context,
	ICredentialStore credentials,
	SearchIndexer search,
	ILogger<AccountCredentialCleanupService> logger
)
{
	public async Task CompleteDatabaseRemovalAsync(Guid accountId, CancellationToken ct = default)
	{
		var account = await context.Accounts.FirstOrDefaultAsync(candidate => candidate.Id == accountId, ct);
		if (account is null)
		{
			return;
		}

		await using var transaction = await context.Database.BeginTransactionAsync(ct);
		await search.RemoveForAccountAsync(accountId, ct);
		await context
			.Mailboxes.Where(mailbox => mailbox.AccountId == accountId)
			.ExecuteUpdateAsync(update => update.SetProperty(mailbox => mailbox.ParentId, (Guid?)null), ct);
		context.Accounts.Remove(account);
		await context.SaveChangesAsync(ct);
		await transaction.CommitAsync(ct);
	}

	public async Task CompleteAsync(Guid accountId, CancellationToken ct = default)
	{
		await CompleteDatabaseRemovalAsync(accountId, ct);
		await credentials.DeleteAsync(accountId, ct);
		await credentials.DeleteSlotAsync(accountId, CredentialSlots.CalDav, ct);
		await credentials.DeleteSlotAsync(accountId, CredentialSlots.Smtp, ct);
		await credentials.DeleteSlotAsync(accountId, CredentialSlots.GmailClientSecret, ct);

		await context
			.AccountCredentialCleanups.Where(cleanup => cleanup.AccountId == accountId)
			.ExecuteDeleteAsync(ct);
	}

	public async Task CompleteIfPendingAsync(Guid accountId, CancellationToken ct = default)
	{
		if (
			await context.AccountCredentialCleanups.AnyAsync(
				cleanup => cleanup.AccountId == accountId,
				ct
			)
		)
		{
			await CompleteAsync(accountId, ct);
		}
	}

	/// <summary>
	/// Startup recovery for removals committed before the external credential store completed.
	/// One failed store must not prevent unrelated cleanup intents from being attempted.
	/// </summary>
	public async Task CompletePendingAsync(CancellationToken ct = default)
	{
		var accountIds = await context
			.AccountCredentialCleanups.Select(cleanup => cleanup.AccountId)
			.ToListAsync(ct);

		foreach (var accountId in accountIds)
		{
			try
			{
				await CompleteAsync(accountId, ct);
			}
			catch (Exception exception) when (exception is not OperationCanceledException)
			{
				logger.LogWarning(exception, "Credential cleanup for removed account {AccountId} will retry.", accountId);
			}
		}
	}
}
