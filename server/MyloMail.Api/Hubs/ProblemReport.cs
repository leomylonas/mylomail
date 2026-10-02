using Microsoft.EntityFrameworkCore;
using MyloMail.Api.Contracts;
using MyloMail.Api.Domain;
using MyloMail.Api.Persistence;

namespace MyloMail.Api.Hubs;

/// <summary>
/// Gathers the failures background work has recorded, so the user can see them (§7). Reads the
/// same durable state the sidebar's availability is derived from; it records nothing itself.
/// </summary>
internal static class ProblemReport
{
	public static async Task<IReadOnlyList<ProblemDto>> LoadAsync(MyloMailDbContext context, CancellationToken ct = default)
	{
		var problems = new List<ProblemDto>();
		problems.AddRange(await MessageDownloadsAsync(context, ct));
		problems.AddRange(await MessagesRetryingAsync(context, ct));
		problems.AddRange(await FolderSyncAsync(context, ct));
		return problems;
	}

	private static async Task<IEnumerable<ProblemDto>> MessageDownloadsAsync(MyloMailDbContext context, CancellationToken ct)
	{
		var failed = await context
			.MessageContentStates.Where(state => state.Status == ContentStatus.Failed)
			.AsNoTracking()
			.ToListAsync(ct);
		if (failed.Count == 0) return [];

		var ids = failed.Select(state => state.MessageId).ToList();
		var messages = await context.Messages.Where(message => ids.Contains(message.Id)).AsNoTracking().ToDictionaryAsync(m => m.Id, ct);
		var accountsById = await context.Accounts.AsNoTracking().ToDictionaryAsync(a => a.Id, ct);
		var folders = await context
			.MessageMailboxes.Where(occurrence => ids.Contains(occurrence.MessageId))
			.Join(context.Mailboxes, occurrence => occurrence.MailboxId, mailbox => mailbox.Id, (occurrence, mailbox) => new { occurrence.MessageId, mailbox.Id, mailbox.Name })
			.AsNoTracking()
			.ToListAsync(ct);
		var firstFolder = folders.GroupBy(row => row.MessageId).ToDictionary(group => group.Key, group => group.OrderBy(row => row.Name).First());

		return failed
			.Where(state => messages.ContainsKey(state.MessageId))
			.Select(state =>
			{
				var message = messages[state.MessageId];
				firstFolder.TryGetValue(state.MessageId, out var folder);
				var sender = message.From.FirstOrDefault();
				var account = accountsById[message.AccountId];
				var tooLarge = Content.ContentAcquisition.IsTooLarge(message.SizeEstimate, account);
				return new ProblemDto(
					$"message:{state.MessageId}",
					tooLarge ? ProblemKind.MessageTooLarge : ProblemKind.MessageDownload,
					message.AccountId,
					tooLarge ? "This message is over the download limit" : "This message could not be downloaded",
					// Worded from the limit in force now, not whatever was recorded when it first failed.
					tooLarge
						? Content.ContentAcquisition.TooLargeMessage(message.SizeEstimate!.Value, account.MaxMessageDownloadMegabytes)
						: state.LastError,
					folder?.Id,
					folder?.Name,
					message.Id,
					message.Subject,
					sender is null ? null : sender.Name is { Length: > 0 } name ? $"{name} <{sender.Email}>" : sender.Email,
					message.ReceivedAt,
					state.Attempts,
					// A message above the download cap fails identically every time.
					CanRetry: !tooLarge
				);
			})
			.OrderByDescending(problem => problem.ReceivedAt);
	}

	private static async Task<IEnumerable<ProblemDto>> MessagesRetryingAsync(MyloMailDbContext context, CancellationToken ct)
	{
		var retrying = await context
			.MessageContentStates.Where(state =>
				(state.Status == ContentStatus.Queued || state.Status == ContentStatus.Fetching) && state.LastError != null)
			.Join(context.Messages, state => state.MessageId, message => message.Id, (state, message) => new { state, message })
			.AsNoTracking()
			.ToListAsync(ct);
		return retrying.Select(row => new ProblemDto(
			$"retrying:{row.message.Id}",
			ProblemKind.MessageRetrying,
			row.message.AccountId,
			"This message is being downloaded again",
			row.state.LastError,
			null,
			null,
			row.message.Id,
			row.message.Subject,
			null,
			row.message.ReceivedAt,
			row.state.Attempts,
			CanRetry: false
		));
	}

	private static async Task<IEnumerable<ProblemDto>> FolderSyncAsync(MyloMailDbContext context, CancellationToken ct)
	{
		var mailboxes = await context.Mailboxes.AsNoTracking().ToListAsync(ct);
		var byId = mailboxes.ToDictionary(mailbox => mailbox.Id);
		var problems = new List<ProblemDto>();

		void Add(Guid accountId, Guid? mailboxId, string what, string? detail)
		{
			var name = mailboxId is { } id && byId.TryGetValue(id, out var mailbox) ? mailbox.Name : null;
			problems.Add(
				new ProblemDto(
					$"folder:{accountId}:{mailboxId}:{what}",
					ProblemKind.FolderSync,
					accountId,
					name is null ? what : $"{name}: {what}",
					detail,
					mailboxId,
					name,
					null,
					null,
					null,
					null,
					null,
					CanRetry: false
				)
			);
		}

		foreach (var state in await context.MailboxTopologySyncStates.Where(s => s.LastError != null).AsNoTracking().ToListAsync(ct))
			Add(state.AccountId, null, "The folder list could not be updated", state.LastError);
		foreach (var state in await context.MailboxCoverageStates.Where(s => s.Status == CoverageStatus.Failed || s.LastError != null).AsNoTracking().ToListAsync(ct))
			if (byId.TryGetValue(state.MailboxId, out var mailbox))
				Add(mailbox.AccountId, mailbox.Id, "Downloading mail failed", state.LastError);
		foreach (var state in await context.ChangeStreamStates.Where(s => s.LastError != null).AsNoTracking().ToListAsync(ct))
			Add(state.AccountId, state.MailboxId, "New changes could not be read", state.LastError);
		foreach (var state in await context.IntegrityReconciliationStates.Where(s => s.LastError != null).AsNoTracking().ToListAsync(ct))
			if (byId.TryGetValue(state.MailboxId, out var mailbox))
				Add(mailbox.AccountId, mailbox.Id, "The consistency check failed", state.LastError);
		return problems;
	}
}
