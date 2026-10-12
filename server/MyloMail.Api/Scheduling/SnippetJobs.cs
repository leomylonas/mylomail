using Hangfire;
using Microsoft.EntityFrameworkCore;
using MyloMail.Api.Content;
using MyloMail.Api.Domain;
using MyloMail.Api.Persistence;

namespace MyloMail.Api.Scheduling;

/// <summary>
/// Rebuilds the list previews of messages whose preview was computed here from the body — every
/// IMAP message, since IMAP supplies none — with the current <see cref="SnippetBuilder"/>.
/// </summary>
/// <remarks>
/// Gmail and Graph previews come from the provider and are left alone. The work is derived data
/// and idempotent, so it is safe to interrupt: the completion marker is written only at the end
/// and a restart simply starts again.
/// </remarks>
[AutomaticRetry(Attempts = 0)]
public sealed class SnippetJobs(MyloMailDbContext context, ILogger<SnippetJobs> logger)
{
	public const int CurrentVersion = 2;
	private const int BatchSize = 500;

	public async Task RebuildAsync(CancellationToken ct = default)
	{
		var done = 0;
		var changed = 0;
		var lastId = Guid.Empty;
		while (true)
		{
			var batch = await context
				.Messages.Where(m => m.Id > lastId)
				.Where(m => context.Accounts.Any(a => a.Id == m.AccountId && a.ProviderType == ProviderType.Imap))
				.OrderBy(m => m.Id)
				.Take(BatchSize)
				.Select(m => new { m.Id, m.Snippet })
				.ToListAsync(ct);
			if (batch.Count == 0)
			{
				break;
			}

			var ids = batch.Select(row => row.Id).ToList();
			var bodies = await context
				.MessageBodies.AsNoTracking()
				.Where(b => ids.Contains(b.MessageId))
				.ToDictionaryAsync(b => b.MessageId, ct);
			foreach (var row in batch)
			{
				if (!bodies.TryGetValue(row.Id, out var body))
				{
					continue;
				}

				var rebuilt = SnippetBuilder.Build(body.TextBody, body.HtmlBody);
				if (rebuilt.Length > 0 && rebuilt != row.Snippet)
				{
					await context
						.Messages.Where(m => m.Id == row.Id)
						.ExecuteUpdateAsync(setters => setters.SetProperty(m => m.Snippet, rebuilt), ct);
					changed++;
				}
			}

			done += batch.Count;
			lastId = batch[^1].Id;
		}

		var settings = await context.AppSettings.SingleOrDefaultAsync(ct);
		if (settings is null)
		{
			settings = new AppSettings();
			context.AppSettings.Add(settings);
		}

		settings.SnippetBackfillVersion = CurrentVersion;
		await context.SaveChangesAsync(ct);
		logger.LogInformation("Rebuilt list previews: {Changed} of {Count} messages changed.", changed, done);
	}
}
