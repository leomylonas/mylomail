using System.Collections.Concurrent;
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
	IDraftDispatcher dispatcher,
	IMailProviderFactory providers,
	IHubEvents events,
	ILogger<DraftSyncService> logger,
	IFaultInjector faults
)
{

	// One app process owns every scheduled worker. This closes the interval between a durable
	// creation dispatch and its provider call without mistaking an active worker for a crash.
	// A hard restart clears it, which is precisely when durable recovery is required (§6).
	private static readonly ConcurrentDictionary<Guid, byte> activeInitialCreates = [];
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

			var initialCreateGuardHeld = false;
			var ownsInitialCreate = false;
			try
			{
				var provider = providers.For(account);

				// Older local and provider-materialised drafts predate the recovery field.
				// Establish their stable MIME identity before either an update or a create.
				if (draft.StableMessageId is null)
				{
					var proposedStableId = $"<{Guid.NewGuid():N}@mylomail.local>";
					await context
						.Drafts.Where(d => d.Id == draft.Id && d.StableMessageId == null)
						.ExecuteUpdateAsync(setters => setters.SetProperty(d => d.StableMessageId, proposedStableId), ct);
					await context.Entry(draft).ReloadAsync(ct);
				}
				var stableMessageId = draft.StableMessageId
					?? throw new InvalidOperationException("Draft recovery identity was not persisted.");

				if (account.ProviderType == ProviderType.Gmail
					&& draft.ProviderDraftId is { } legacyDraftId
					&& draft.ProviderMessageId is null)
				{
					var recoveredLegacy = await provider.FindDraftAsync(
						account,
						stableMessageId,
						ct,
						RemoteDraftMaterializer.MaximumRawDraftBytes
					);
					if (recoveredLegacy is null)
					{
						draft.SyncConflict = true;
						await context.SaveChangesAsync(ct);
						continue;
					}
					draft.ProviderMessageId = recoveredLegacy.ProviderMessageId;
					draft.ProviderDraftId = recoveredLegacy.ProviderDraftId ?? legacyDraftId;
					draft.ProviderRevision = recoveredLegacy.ProviderRevision;
					await context.SaveChangesAsync(ct);
					await context.Entry(draft).ReloadAsync(ct);
				}

				if (draft.ProviderDraftId is null)
				{
					if (draft.PushDispatchedForSavedAt is { } dispatchedFor)
					{
						// A second worker in this process observes an active claim, not a crash.
						if (activeInitialCreates.ContainsKey(draft.Id))
						{
							continue;
						}

						var recovered = await provider.FindDraftAsync(
							account,
							stableMessageId,
							ct,
							RemoteDraftMaterializer.MaximumRawDraftBytes
						);
						if (recovered is { ProviderRevision: not null and not "" })
						{
							// The returned revision is attached to server content we have not fetched.
							// Preserve both copies as a conflict rather than using it to overwrite
							// somebody else's intervening edit on the next local save (§15).
							await context
								.Drafts.Where(d => d.Id == draft.Id && d.ProviderDraftId == null && d.PushDispatchedForSavedAt != null)
								.ExecuteUpdateAsync(
									setters => setters
										.SetProperty(d => d.ProviderDraftId, recovered.ProviderDraftId)
										.SetProperty(d => d.ProviderMessageId, recovered.ProviderMessageId ?? recovered.ProviderDraftId)
										.SetProperty(d => d.ProviderRevision, recovered.ProviderRevision)
										.SetProperty(d => d.PushedAt, dispatchedFor)
										.SetProperty(d => d.PushDispatchedForSavedAt, (DateTimeOffset?)null)
										.SetProperty(d => d.SyncConflict, true),
									ct
								);
							await context.Entry(draft).ReloadAsync(ct);
							await events.DraftUpdatedAsync(draft.Id);
							pushed++;
							continue;
						}

						// Empty/incomplete observations cannot prove the call never reached the
						// provider, so a second create would be an untracked duplicate (§6).
						draft.SyncConflict = true;
						await context.SaveChangesAsync(ct);
						await events.DraftUpdatedAsync(draft.Id);
						continue;
					}

					// Register before the durable claim. Every worker shares this process-wide
					// guard; after a hard restart it is gone and the marker is recovered above.
					if (!activeInitialCreates.TryAdd(draft.Id, 0))
					{
						continue;
					}
					initialCreateGuardHeld = true;
					var claimed = await context
						.Drafts.Where(d => d.Id == draft.Id && d.ProviderDraftId == null && d.PushDispatchedForSavedAt == null)
						.ExecuteUpdateAsync(
							setters => setters.SetProperty(d => d.PushDispatchedForSavedAt, sending),
							ct
						);
					if (claimed == 0)
					{
						continue;
					}
					ownsInitialCreate = true;
					await context.Entry(draft).ReloadAsync(ct);
				}

				var result = await provider.CreateOrUpdateDraftAsync(account, draft, draft.ProviderRevision, ct);
				if (draft.ProviderDraftId is null && string.IsNullOrEmpty(result.ProviderRevision))
				{
					throw new InvalidOperationException("Initial draft creation requires a provider revision.");
				}
				faults.Reached(FaultPoints.DraftPushAfterProviderCallBeforeCommit);

				if (ownsInitialCreate)
				{
					var committed = await context
						.Drafts.Where(d =>
							d.Id == draft.Id
							&& d.ProviderDraftId == null
							&& d.PushDispatchedForSavedAt == sending
						)
						.ExecuteUpdateAsync(
							setters => setters
								.SetProperty(d => d.ProviderDraftId, result.ProviderDraftId)
								.SetProperty(d => d.ProviderMessageId, result.ProviderMessageId ?? result.ProviderDraftId)
								.SetProperty(d => d.ProviderRevision, result.ProviderRevision)
								.SetProperty(d => d.PushedAt, sending)
								.SetProperty(d => d.PushDispatchedForSavedAt, (DateTimeOffset?)null)
								.SetProperty(d => d.SyncConflict, false),
							ct
						);
					if (committed != 1)
					{
						throw new InvalidOperationException("Initial draft creation lost its durable claim.");
					}
					await context.Entry(draft).ReloadAsync(ct);
				}
				else
				{
					draft.ProviderDraftId = result.ProviderDraftId;
					draft.ProviderMessageId = result.ProviderMessageId ?? result.ProviderDraftId;
					draft.ProviderRevision = result.ProviderRevision;
					draft.PushedAt = sending;
				}
				pushed++;
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
				draft.PushDispatchedForSavedAt = null;
				// A provider without server-side drafts leaves local authoring intact.
				return pushed;
			}
			catch (CredentialStoreUnavailableException ex)
			{
				draft.PushDispatchedForSavedAt = null;
				account.AuthState = AuthState.CredentialStoreUnavailable;
				account.LastAuthError = ex.Message;
				await context.SaveChangesAsync(ct);
				await Accounts.AccountDtoFactory.AnnounceStatusAsync(context, events, account, ct);
				logger.LogWarning("Account {AccountId}'s credential store could not be reached.", accountId);
				return pushed;
			}

			finally
			{
				if (initialCreateGuardHeld)
				{
					activeInitialCreates.TryRemove(draft.Id, out _);
				}
			}

			// Saved per-draft, not batched after the loop (§16): a crash between two drafts'
			// pushes must not lose the ProviderDraftId a provider call already committed
			// server-side, which would otherwise leave that draft looking un-pushed and create
			// an orphaned duplicate on retry.
			await context.SaveChangesAsync(ct);
			await events.DraftUpdatedAsync(draft.Id);
			await context.Entry(draft).ReloadAsync(ct);
			if (draft.PushedAt is { } pushedAt && draft.SavedAt > pushedAt)
			{
				dispatcher.RequestPush(draft.AccountId);
			}
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

	/// <summary>
	/// Deletes the remote draft as an explicit user conflict decision. Unlike ordinary local
	/// deletion, failure is surfaced and no new server draft may be created until this one is
	/// conclusively gone. Gmail and Graph therefore omit the knowingly stale precondition;
	/// IMAP retains UIDVALIDITY, which is its identity fence (§1, §15).
	/// </summary>
	public async Task RemoveForReplacementAsync(
		Guid accountId,
		string providerDraftId,
		string? providerRevision,
		CancellationToken ct = default
	)
	{
		var account = await context.Accounts.FirstAsync(a => a.Id == accountId, ct);
		var expectedRevision = account.ProviderType == ProviderType.Imap ? providerRevision : null;
		await providers.For(account).DeleteDraftAsync(account, providerDraftId, expectedRevision, ct);
	}

	/// <summary>Removes the server's copy when a draft is deleted or sent.</summary>
	public async Task RemoveRemoteAsync(
		Guid accountId,
		string providerDraftId,
		string? providerRevision,
		CancellationToken ct = default
	)
	{
		var account = await context.Accounts.FirstOrDefaultAsync(a => a.Id == accountId, ct);
		if (account is null)
		{
			return;
		}

		try
		{
			// IMAP UIDs are valid only under their observed UIDVALIDITY; other providers'
			if (account.ProviderType == ProviderType.Imap
				&& (providerRevision is null || !Providers.Imap.ImapMailProvider.TryParseDraftRevision(providerRevision, out _, out _)))
			{
				logger.LogWarning("Remote IMAP draft cleanup skipped because UIDVALIDITY is unavailable for {ProviderDraftId}.", providerDraftId);
				return;
			}
			var expectedRevision = account.ProviderType == ProviderType.Imap ? providerRevision : null;
			await providers.For(account).DeleteDraftAsync(account, providerDraftId, expectedRevision, ct);
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			// Best-effort by design (see the summary above): a network blip, an auth failure, a
			// locked credential store, or any other provider rejection here must not surface as
			// though the delete/resolve/send the caller asked for had failed — DeleteAsync has
			// already committed the local removal by the time this runs, and ResolveConflictAsync's
			// keepMine already made its decision. Narrowly catching only NotSupportedException/
			// ProviderConflictException let every other exception type still block the caller,
			// contradicting this method's own stated contract.
			logger.LogInformation(ex, "Remote draft {ProviderDraftId} was not removed.", providerDraftId);
		}
	}
}
