using Microsoft.EntityFrameworkCore;
using MyloMail.Api.Domain;
using MyloMail.Api.Persistence;
using MyloMail.Api.Providers;
using MyloMail.Api.Providers.Contracts;

namespace MyloMail.Api.Sync;

/// <summary>
/// Periodic reconciliation for facts a valid incremental cursor cannot express (§3).
/// </summary>
/// <remarks>
/// This is intentionally distinct from cursor-invalid resynchronisation. A periodic run is
/// routine degraded-IMAP maintenance: it compares the server UID set to local occurrences
/// and, on the basic tier, scans flags. It does not reset a cursor or claim anything was
/// broken.
/// </remarks>
public sealed class IntegrityReconciliationService(

	MyloMailDbContext context,
	IMailProviderFactory providers,
	MessageIngestor ingestor,
	TimeProvider clock,
	ILogger<IntegrityReconciliationService> logger
)
{
	public async Task<bool> RequiredAsync(Account account, CancellationToken ct = default) =>
		await Task.FromResult(Required(providers.For(account).Capabilities));

	public static bool Required(ProviderCapabilities capabilities) =>
		capabilities.Type == ProviderType.Imap
		&& (!capabilities.ReportsExpungesIncrementally || !capabilities.SupportsIncrementalFlagChanges);

	public async Task ReconcileAsync(Account account, Mailbox mailbox, CancellationToken ct = default)
	{
		var provider = providers.For(account);
		if (!Required(provider.Capabilities))
		{
			return;
		}

		var generations = GenerationSnapshot.Capture([mailbox]);
		var known = await context.MessageMailboxes
			.Where(o => o.MailboxId == mailbox.Id)
			.Select(o => new MessageOccurrenceRef(o.MessageId, o.MailboxId, o.ProviderOccurrenceId))
			.ToListAsync(ct);
		var snapshot = await provider.GetMailboxIntegritySnapshotAsync(account, mailbox, known, ct);

		var strategy = context.Database.CreateExecutionStrategy();
		await strategy.ExecuteAsync(async () =>
		{
			await using var transaction = await context.Database.BeginTransactionAsync(ct);
			foreach (var missing in known.Where(o => !snapshot.ExistingOccurrenceIds.Contains(o.ProviderOccurrenceId)))
			{
				await ingestor.RemoveOccurrenceAsync(mailbox, missing.ProviderOccurrenceId, generations, ct);
			}

			foreach (var change in snapshot.FlagChanges)
			{
				await ingestor.ApplyFlagChangeAsync(mailbox, change, generations, ct);
			}

			var state = await context.IntegrityReconciliationStates.FirstOrDefaultAsync(s => s.MailboxId == mailbox.Id, ct);
			if (state is null)
			{
				state = new IntegrityReconciliationState { MailboxId = mailbox.Id };
				context.IntegrityReconciliationStates.Add(state);
			}

			state.LastReconciledAt = clock.GetUtcNow();
			state.LastError = null;
			await context.SaveChangesAsync(ct);
			await transaction.CommitAsync(ct);
		});

		logger.LogInformation("Completed periodic integrity reconciliation for mailbox {MailboxId}.", mailbox.Id);
	}
}
