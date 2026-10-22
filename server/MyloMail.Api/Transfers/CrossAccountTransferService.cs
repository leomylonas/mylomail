using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using MyloMail.Api.Contracts;
using MyloMail.Api.Credentials;
using MyloMail.Api.Domain;
using MyloMail.Api.Errors;
using MyloMail.Api.FaultInjection;
using MyloMail.Api.Hubs;
using MyloMail.Api.Mutations;
using MyloMail.Api.Persistence;
using MyloMail.Api.Providers;
using MyloMail.Api.Providers.Contracts;
using MyloMail.Api.Scheduling;

namespace MyloMail.Api.Transfers;

/// <summary>
/// Moves messages to a folder of another account: copy first, remove the original only once the
/// copy is known to exist (<see cref="CrossAccountTransfer"/>).
/// </summary>
/// <remarks>
/// <para>
/// The import follows the dispatch boundary every irreversible provider call does (§6): the
/// transfer is marked <see cref="CrossAccountTransferState.Dispatched"/> synchronously and durably
/// immediately before it, so a crash at any later point is known to be "unknown" rather than
/// "never happened". Unknown is settled by looking for the copy by <c>Message-ID</c> in the target
/// mailbox, and only if it is not there is the import tried again. A message with no
/// <c>Message-ID</c> cannot be looked for, so it is never retried after an unknown outcome: the
/// transfer fails and the original stays where it is.
/// </para>
/// <para>
/// The original is removed afterwards as an ordinary mutation on the source account, so a failure
/// to remove it is that account's usual recoverable delete and can never lose the message.
/// </para>
/// </remarks>
public sealed class CrossAccountTransferService(
	MyloMailDbContext context,
	IMailProviderFactory providers,
	MutationQueue mutations,
	ITransferDispatcher dispatcher,
	IFaultInjector faults,
	IHubEvents events,
	TimeProvider clock,
	ILogger<CrossAccountTransferService> logger
)
{
	/// <summary>
	/// How long an import whose outcome is unknown is looked for before it is treated as not having
	/// happened. Providers index a new message a little after storing it (Gmail's search, an IMAP
	/// server's header index), so a single empty answer straight after the call proves nothing; only
	/// an answer that is still empty after this long is evidence, and only then is it imported again.
	/// </summary>
	internal static readonly TimeSpan UnknownOutcomeWindow = TimeSpan.FromMinutes(3);

	/// <summary>Imports tried for one message before it is given up on: a provider that keeps refusing it is not asked forever.</summary>
	internal const int MaximumAttempts = 4;

	/// <summary>
	/// One run at a time per target account. Every enqueue asks for a run and an account's queue has
	/// several workers, so two runs would otherwise both pick up the same waiting transfer.
	/// </summary>
	private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> Runs = new();

	/// <summary>
	/// Records the intent to move these messages and asks for it to be carried out. Rejects the whole
	/// request, before anything is recorded, when it cannot work.
	/// </summary>
	/// <returns>
	/// One entry per transfer started (a message already being moved is not started twice), carrying the
	/// transfer's id. That id is also the id of the mutation that later moves the original to Trash, so
	/// the renderer can hide the message under it and let that mutation's settlement release it.
	/// </returns>
	public async Task<IReadOnlyList<MutationEnqueueAcceptanceDto>> EnqueueAsync(
		Guid sourceAccountId,
		IReadOnlyList<Guid> messageIds,
		Guid targetAccountId,
		Guid targetMailboxId,
		CancellationToken ct = default
	)
	{
		if (sourceAccountId == targetAccountId)
		{
			throw new MutationHubException(ErrorCategory.Validation, "Choose a folder in a different account.");
		}

		var target = await context.Accounts.AsNoTracking().FirstOrDefaultAsync(a => a.Id == targetAccountId, ct)
			?? throw new MutationHubException(ErrorCategory.Validation, "That account no longer exists.");
		var mailbox = await context.Mailboxes.AsNoTracking().FirstOrDefaultAsync(m => m.Id == targetMailboxId, ct);
		if (mailbox is null || mailbox.AccountId != targetAccountId || mailbox.ProviderMailboxId is null)
		{
			throw new MutationHubException(ErrorCategory.Validation, "That folder is not available in the target account.");
		}

		if (mailbox.SpecialUse == SpecialUse.Drafts)
		{
			throw new MutationHubException(ErrorCategory.Validation, "Mail cannot be moved into Drafts.");
		}

		// Some providers need a permission the sign-in does not carry (Microsoft 365's mailbox import).
		// Asked for here, where the user has just asked for the move and can answer a consent screen.
		if (providers.For(target) is IImportAuthorization authorization)
		{
			try
			{
				await authorization.EnsureImportAuthorizedAsync(target, ct);
			}
			catch (InvalidOperationException ex)
			{
				throw new MutationHubException(ErrorCategory.Validation, ex.Message);
			}
		}

		var messages = await context
			.Messages.AsNoTracking()
			.Where(m => messageIds.Contains(m.Id))
			.Select(m => new
			{
				m.Id,
				m.AccountId,
				m.MessageIdHeader,
				m.IsRead,
				m.IsFlagged,
			})
			.ToListAsync(ct);
		if (messages.Count != messageIds.Distinct().Count() || messages.Any(m => m.AccountId != sourceAccountId))
		{
			throw new MutationHubException(ErrorCategory.Validation, "Some of those messages no longer exist in this account.");
		}

		var active = await context
			.CrossAccountTransfers.Where(t =>
				messageIds.Contains(t.MessageId)
				&& (t.State == CrossAccountTransferState.Pending
					|| t.State == CrossAccountTransferState.Dispatched
					|| t.State == CrossAccountTransferState.Imported)
			)
			.Select(t => t.MessageId)
			.ToListAsync(ct);

		var started = new List<MutationEnqueueAcceptanceDto>();
		foreach (var message in messages.Where(m => !active.Contains(m.Id)))
		{
			var transfer = new CrossAccountTransfer
			{
				Id = Guid.NewGuid(),
				SourceAccountId = sourceAccountId,
				MessageId = message.Id,
				TargetAccountId = targetAccountId,
				TargetMailboxId = targetMailboxId,
				MessageIdHeader = message.MessageIdHeader,
				IsRead = message.IsRead,
				IsFlagged = message.IsFlagged,
				State = CrossAccountTransferState.Pending,
				CreatedAt = clock.GetUtcNow(),
			};
			context.CrossAccountTransfers.Add(transfer);
			try
			{
				await context.SaveChangesAsync(ct);
				started.Add(new MutationEnqueueAcceptanceDto(message.Id, transfer.Id));
			}
			catch (DbUpdateException)
			{
				// Another caller started a move of this message since the check above: the unique index
				// on unfinished transfers says there is already one, which is the answer.
				context.Entry(transfer).State = EntityState.Detached;
			}
		}

		if (started.Count > 0)
		{
			dispatcher.RequestRun(targetAccountId);
		}

		return started;
	}

	/// <summary>
	/// Carries out an account's transfers: settles any whose outcome is unknown first, then imports
	/// what is waiting, then removes the originals of what has been imported.
	/// </summary>
	/// <returns>True while anything is still unfinished, so the caller runs again later.</returns>
	public async Task<bool> RunAsync(Account target, CancellationToken ct = default)
	{
		var turn = Runs.GetOrAdd(target.Id, static _ => new SemaphoreSlim(1, 1));
		await turn.WaitAsync(ct);
		try
		{
			foreach (var transfer in await TransfersAsync(target.Id, CrossAccountTransferState.Dispatched, ct))
			{
				await SettleUnknownAsync(target, transfer, ct);
			}

			foreach (var transfer in await TransfersAsync(target.Id, CrossAccountTransferState.Pending, ct))
			{
				await ImportAsync(target, transfer, ct);
			}

			foreach (var transfer in await TransfersAsync(target.Id, CrossAccountTransferState.Imported, ct))
			{
				await RemoveOriginalAsync(transfer, ct);
			}

			return await context.CrossAccountTransfers.AnyAsync(
				t =>
					t.TargetAccountId == target.Id
					&& (t.State == CrossAccountTransferState.Pending
						|| t.State == CrossAccountTransferState.Dispatched
						|| t.State == CrossAccountTransferState.Imported),
				ct
			);
		}
		finally
		{
			turn.Release();
		}
	}

	private async Task<List<CrossAccountTransfer>> TransfersAsync(Guid targetAccountId, CrossAccountTransferState state, CancellationToken ct) =>
		// Ordered here: SQLite cannot order DateTimeOffset values.
		(await context.CrossAccountTransfers.Where(t => t.TargetAccountId == targetAccountId && t.State == state).ToListAsync(ct))
			.OrderBy(t => t.CreatedAt)
			.ToList();

	private async Task ImportAsync(Account target, CrossAccountTransfer transfer, CancellationToken ct)
	{
		if (transfer.Attempts >= MaximumAttempts)
		{
			await FailAsync(
				transfer,
				"The other account did not accept this message after several tries. The original has not been touched.",
				ct
			);
			return;
		}

		// Everything before the dispatched write only reads: a failure here leaves the transfer
		// Pending and nothing sent, so it can simply be tried again.
		byte[]? raw;
		var mailbox = await context.Mailboxes.AsNoTracking().FirstOrDefaultAsync(m => m.Id == transfer.TargetMailboxId, ct);
		try
		{
			raw = await RawAsync(transfer, ct);
			if (raw is not null && mailbox?.ProviderMailboxId is not null && transfer.BaselineCopies is null)
			{
				transfer.BaselineCopies = string.IsNullOrWhiteSpace(transfer.MessageIdHeader)
					? 0
					: await CopiesInTargetAsync(target, transfer, mailbox, ct);
				await context.SaveChangesAsync(ct);
			}
		}
		catch (Exception ex)
			when (ex is TransferSourceException
				or ProviderThrottledException
				or ProviderAuthenticationException
				or ProviderNotConfiguredException
				or CredentialStoreUnavailableException)
		{
			// The source account's refusal (wrapped) or the target's own: the job applies it to the
			// right account. The transfer stays Pending.
			throw;
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			transfer.LastError = ex.Message;
			await context.SaveChangesAsync(ct);
			logger.LogWarning(ex, "Transfer {TransferId} could not be prepared yet.", transfer.Id);
			return;
		}

		if (raw is null || mailbox is null)
		{
			await FailAsync(transfer, raw is null ? "The message is no longer available in its account." : "The target folder no longer exists.", ct);
			return;
		}

		// Synchronous, durable and atomic, immediately before the irreversible call (§6): it only takes
		// effect if the transfer is still Pending, so two runs can never both import it.
		var now = clock.GetUtcNow();
		var claimed = await context
			.CrossAccountTransfers.Where(t => t.Id == transfer.Id && t.State == CrossAccountTransferState.Pending)
			.ExecuteUpdateAsync(
				set =>
					set.SetProperty(t => t.State, CrossAccountTransferState.Dispatched)
						.SetProperty(t => t.DispatchedAt, now)
						.SetProperty(t => t.Attempts, t => t.Attempts + 1),
				ct
			);
		if (claimed == 0)
		{
			return;
		}

		await context.Entry(transfer).ReloadAsync(ct);
		faults.Reached(FaultPoints.TransferAfterDispatchedBeforeImport);

		try
		{
			await providers.For(target).ImportMessageAsync(target, mailbox, raw, transfer.IsRead, transfer.IsFlagged, ct);
		}
		catch (Exception ex)
			when (ex is ProviderThrottledException
				or ProviderAuthenticationException
				or ProviderNotConfiguredException
				or CredentialStoreUnavailableException)
		{
			// Refused before anything was stored: a definite outcome, not an unknown one. The refused
			// attempt does not count against the message.
			transfer.State = CrossAccountTransferState.Pending;
			transfer.Attempts--;
			await context.SaveChangesAsync(ct);
			throw;
		}
		catch (NotSupportedException ex)
		{
			await FailAsync(transfer, ex.Message, ct);
			return;
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			// The request may have been carried out. Left Dispatched, to be settled by looking for
			// the copy; never retried blindly.
			transfer.LastError = ex.Message;
			await context.SaveChangesAsync(ct);
			logger.LogWarning(ex, "Transfer {TransferId} may or may not have reached the target account.", transfer.Id);
			if (ConnectivityMonitor.IsNetworkFailure(ex))
			{
				throw;
			}

			return;
		}

		faults.Reached(FaultPoints.TransferAfterImportBeforeRecorded);
		await MarkImportedAsync(transfer, ct);
		await RemoveOriginalAsync(transfer, ct);
	}

	/// <summary>
	/// The message exactly as received: the copy held locally when there is one, otherwise read from
	/// the source account now. Null when it is gone from both. Reading never changes either account,
	/// so a failure here is a plain "not yet".
	/// </summary>
	private async Task<byte[]?> RawAsync(CrossAccountTransfer transfer, CancellationToken ct)
	{
		var local = await context
			.MessageRaws.AsNoTracking()
			.Where(r => r.MessageId == transfer.MessageId)
			.Select(r => r.Content)
			.FirstOrDefaultAsync(ct);
		if (local is not null)
		{
			return local;
		}

		var occurrence = await (
			from membership in context.MessageMailboxes.AsNoTracking()
			where membership.MessageId == transfer.MessageId
			orderby membership.MailboxId
			select new { membership.MailboxId, membership.ProviderOccurrenceId }
		).FirstOrDefaultAsync(ct);
		var source = await context.Accounts.AsNoTracking().FirstOrDefaultAsync(a => a.Id == transfer.SourceAccountId, ct);
		if (occurrence is null || source is null)
		{
			return null;
		}

		try
		{
			var fetched = await providers
				.For(source)
				.FetchRawMessageAsync(
					source,
					new MessageOccurrenceRef(transfer.MessageId, occurrence.MailboxId, occurrence.ProviderOccurrenceId),
					ct
				);
			return fetched.RawBytes;
		}
		catch (Exception ex)
			when (ex is ProviderThrottledException
				or ProviderAuthenticationException
				or ProviderNotConfiguredException
				or CredentialStoreUnavailableException)
		{
			// This is the source account's provider refusing, not the target's: the job must apply a
			// throttle or a sign-in failure to the account that actually raised it.
			throw new TransferSourceException(transfer.SourceAccountId, ex);
		}
	}

	/// <summary>
	/// A transfer left <see cref="CrossAccountTransferState.Dispatched"/> may or may not have
	/// reached the target. Look for the copy; import again only once it has been absent for long
	/// enough that its absence means something.
	/// </summary>
	private async Task SettleUnknownAsync(Account target, CrossAccountTransfer transfer, CancellationToken ct)
	{
		if (string.IsNullOrWhiteSpace(transfer.MessageIdHeader))
		{
			await FailAsync(
				transfer,
				"It could not be confirmed whether this message reached the other account, and it has no Message-ID to look for it by, so it was not tried again. The original has not been touched; check the other account before moving it again.",
				ct
			);
			return;
		}

		var mailbox = await context.Mailboxes.AsNoTracking().FirstOrDefaultAsync(m => m.Id == transfer.TargetMailboxId, ct);
		if (mailbox?.ProviderMailboxId is null)
		{
			await FailAsync(transfer, "The target folder no longer exists.", ct);
			return;
		}

		// More copies than the folder held before the import: the import happened. (Not merely "a copy
		// exists": a message already there with the same Message-ID would otherwise look like it.)
		if (await CopiesInTargetAsync(target, transfer, mailbox, ct) > (transfer.BaselineCopies ?? 0))
		{
			await MarkImportedAsync(transfer, ct);
			await RemoveOriginalAsync(transfer, ct);
			return;
		}

		// Absent. A provider indexes a new message a little after storing it, so an empty answer straight
		// after the call is not yet evidence; keep looking until it has been absent for the whole window.
		if (clock.GetUtcNow() - (transfer.DispatchedAt ?? DateTimeOffset.MinValue) < UnknownOutcomeWindow)
		{
			return;
		}

		transfer.State = CrossAccountTransferState.Pending;
		await context.SaveChangesAsync(ct);
	}

	/// <summary>How many messages with this transfer's <c>Message-ID</c> the target folder holds now.</summary>
	private async Task<int> CopiesInTargetAsync(Account target, CrossAccountTransfer transfer, Mailbox mailbox, CancellationToken ct)
	{
		var mailboxes = await context
			.Mailboxes.AsNoTracking()
			.Where(m => m.AccountId == target.Id && m.ProviderMailboxId != null)
			.ToListAsync(ct);
		var found = await providers
			.For(target)
			.FindMessageAsync(target, new MessageLookup(null, transfer.MessageIdHeader, [], mailboxes), ct);
		return found.Sum(dto => dto.Occurrences.Count(o => o.ProviderMailboxId == mailbox.ProviderMailboxId));
	}

	private async Task MarkImportedAsync(CrossAccountTransfer transfer, CancellationToken ct)
	{
		transfer.State = CrossAccountTransferState.Imported;
		transfer.ImportedAt = clock.GetUtcNow();
		transfer.LastError = null;
		await context.SaveChangesAsync(ct);
	}

	/// <summary>
	/// Hands the original to the source account's mutation queue to be moved to its Trash. Safe to
	/// reach twice: a removal already queued since the copy was confirmed is not queued again.
	/// </summary>
	private async Task RemoveOriginalAsync(CrossAccountTransfer transfer, CancellationToken ct)
	{
		faults.Reached(FaultPoints.TransferAfterImportedBeforeSourceRemoval);

		// The removal's own id is the transfer's, so "already queued" is a question about one row and
		// not about clocks: reaching this twice (a crash after queueing) cannot queue it twice.
		var alreadyQueued = await context.MutationItems.AnyAsync(item => item.Id == transfer.Id, ct);
		// A message deleted from the source in the meantime leaves the copy as all that is left, which
		// is the intended end state.
		var originalExists = await context.Messages.AnyAsync(m => m.Id == transfer.MessageId, ct);
		if (!alreadyQueued && originalExists)
		{
			await mutations.EnqueueAsync(
				new MutationItem
				{
					Id = transfer.Id,
					AccountId = transfer.SourceAccountId,
					MessageId = transfer.MessageId,
					OperationKind = MutationOperationKind.MoveToTrash,
				},
				ct
			);
		}

		faults.Reached(FaultPoints.TransferAfterSourceRemovalQueuedBeforeCompleted);

		transfer.State = CrossAccountTransferState.Completed;
		transfer.CompletedAt = clock.GetUtcNow();
		await context.SaveChangesAsync(ct);
	}

	private async Task FailAsync(CrossAccountTransfer transfer, string reason, CancellationToken ct)
	{
		transfer.State = CrossAccountTransferState.Failed;
		transfer.LastError = reason;
		transfer.CompletedAt = clock.GetUtcNow();
		await context.SaveChangesAsync(ct);
		logger.LogWarning("Transfer {TransferId} failed: {Reason}", transfer.Id, reason);
		await events.TransferFailedAsync(transfer.SourceAccountId, transfer.MessageId, transfer.Id, reason);
	}
}

/// <summary>
/// The <i>source</i> account's provider refused while a transfer into another account was reading
/// its message (throttled, signed out). Carries the source account so the job layer applies it there.
/// </summary>
public sealed class TransferSourceException(Guid sourceAccountId, Exception inner)
	: Exception(inner.Message, inner)
{
	public Guid SourceAccountId { get; } = sourceAccountId;
}
