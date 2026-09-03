using Microsoft.EntityFrameworkCore;
using MyloMail.Api.Credentials;
using MyloMail.Api.Domain;
using MyloMail.Api.FaultInjection;
using MyloMail.Api.Hubs;
using MyloMail.Api.Persistence;
using MyloMail.Api.Providers;

namespace MyloMail.Api.Compose;

/// <summary>
/// Pushes local drafts to the server's Drafts folder (§1, §15).
/// </summary>
/// <remarks>
/// <para>
/// A separate job rather than the mutation queue. §6's chain is keyed by
/// <c>(AccountId, MessageId)</c> and its five operations are message operations; a draft has
/// no message and no position in that ordering, so putting it there would mean inventing a
/// second key for a queue whose whole design rests on the first one.
/// </para>
/// <para>
/// Pushing is best-effort and idempotent: the draft's authority is local, and a push that
/// fails leaves it dirty to be retried rather than losing the user's text.
/// </para>
/// </remarks>
public sealed class DraftSyncService(
	MyloMailDbContext context,
	IMailProviderFactory providers,
	IHubEvents events,
	ILogger<DraftSyncService> logger,
	IFaultInjector faults
)
{
	public async Task<int> PushAsync(Guid accountId, CancellationToken ct = default)
	{
		var account = await context.Accounts.FirstOrDefaultAsync(a => a.Id == accountId, ct);
		if (account is null || !account.IsEnabled || account.AuthState == AuthState.NeedsReauth)
		{
			return 0;
		}

		var candidates = await context
			.Drafts.Where(d => d.AccountId == accountId && !d.SyncConflict)
			.ToListAsync(ct);

		// Compared in memory: SQLite cannot compare two DateTimeOffset columns usefully, and
		// this set is small by construction.
		var pending = candidates.Where(d => d.PushedAt is null || d.PushedAt < d.SavedAt).ToList();
		var pushed = 0;

		foreach (var draft in pending)
		{
			draft.FromAddress = await context
				.SendIdentities.Where(i => i.Id == draft.SendIdentityId)
				.Select(i => i.EmailAddress)
				.FirstAsync(ct);

			// Captured before the call: a save landing while this push is in flight must leave
			// the draft dirty, so the timestamp recorded is the one that was actually sent.
			var sending = draft.SavedAt;

			try
			{
				var result = await providers
					.For(account)
					.CreateOrUpdateDraftAsync(account, draft, draft.ProviderRevision, ct);

				draft.ProviderDraftId = result.ProviderDraftId;
				draft.ProviderRevision = result.ProviderRevision;
				draft.PushedAt = sending;
				pushed++;

				faults.Reached(FaultPoints.DraftPushAfterProviderCallBeforeCommit);
			}
			catch (ProviderConflictException ex)
			{
				// Not overwritten. The server's copy is someone's work too, and which survives
				// is the user's decision (§1).
				draft.SyncConflict = true;
				logger.LogWarning(ex, "Draft {DraftId} conflicts with the server's copy.", draft.Id);
			}
			catch (NotSupportedException)
			{
				// A provider without server-side drafts. Local authoring still works, and
				// marking the draft pushed would claim something untrue.
				return pushed;
			}
			catch (CredentialStoreUnavailableException ex)
			{
				// Nothing claims/detaches `account` ahead of this loop, so it is still the
				// tracked, attached instance. Not gated off from retrying (see the self-clear
				// below): the next scheduled push succeeding, once the OS store is reachable
				// again, is the recovery path (see SyncJobs.GuardAsync's matching comment).
				account.AuthState = AuthState.CredentialStoreUnavailable;
				account.LastAuthError = ex.Message;
				await context.SaveChangesAsync(ct);
				await Accounts.AccountDtoFactory.AnnounceStatusAsync(context, events, account, ct);

				logger.LogWarning("Account {AccountId}'s credential store could not be reached.", accountId);
				return pushed;
			}

			// Saved per-draft, not batched after the loop (§16): a crash between two drafts'
			// pushes must not lose the ProviderDraftId a provider call already committed
			// server-side, which would otherwise leave that draft looking un-pushed and create
			// an orphaned duplicate on retry.
			await context.SaveChangesAsync(ct);
			await events.DraftUpdatedAsync(draft.Id);
		}

		if (account.AuthState == AuthState.CredentialStoreUnavailable)
		{
			account.AuthState = AuthState.Connected;
			account.LastAuthError = null;
			await context.SaveChangesAsync(ct);
			await Accounts.AccountDtoFactory.AnnounceStatusAsync(context, events, account, ct);
		}

		return pushed;
	}

	/// <summary>Removes the server's copy when a draft is deleted or sent.</summary>
	public async Task RemoveRemoteAsync(Guid accountId, string providerDraftId, CancellationToken ct = default)
	{
		var account = await context.Accounts.FirstOrDefaultAsync(a => a.Id == accountId, ct);
		if (account is null)
		{
			return;
		}

		try
		{
			await providers.For(account).DeleteDraftAsync(account, providerDraftId, ct);
		}
		catch (Exception ex) when (ex is NotSupportedException or ProviderConflictException)
		{
			// Already gone, or never supported. Either way there is nothing to remove and
			// failing here would block the local deletion the user asked for.
			logger.LogInformation(ex, "Remote draft {ProviderDraftId} was not removed.", providerDraftId);
		}
	}
}
