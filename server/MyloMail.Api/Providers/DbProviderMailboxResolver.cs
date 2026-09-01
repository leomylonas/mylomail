using Microsoft.EntityFrameworkCore;
using MyloMail.Api.Persistence;

namespace MyloMail.Api.Providers;

/// <summary>
/// Resolves local mailbox identity to the provider's identifier from the local database,
/// which is the one source of truth for that mapping (§2).
/// </summary>
/// <remarks>
/// <b>A provider must not cache this.</b> The mapping is resolved at the moment of use so
/// that a queued mutation never carries a provider mailbox id it captured earlier — which is
/// also why a folder rename invalidates no queued mutation, and only deletion does.
/// </remarks>
public sealed class DbProviderMailboxResolver(MyloMailDbContext context) : IProviderMailboxResolver
{
	public string ProviderMailboxId(Guid mailboxId)
	{
		var mailbox =
			context.Mailboxes.AsNoTracking().FirstOrDefault(m => m.Id == mailboxId)
			?? throw new KeyNotFoundException($"Mailbox {mailboxId} is not known locally.");

		// Null is legitimate only for a synthesised intermediate node in a derived Gmail
		// label hierarchy, which has no backing provider object and therefore cannot be the
		// target of a provider call (§1).
		return mailbox.ProviderMailboxId
			?? throw new InvalidOperationException(
				$"Mailbox {mailboxId} is a synthesised hierarchy node with no provider object, "
					+ "so it cannot be addressed on the server."
			);
	}

	public string LocalPath(Guid mailboxId, char separator)
	{
		var segments = new List<string>();
		var current = mailboxId;

		while (true)
		{
			var mailbox =
				context.Mailboxes.AsNoTracking().FirstOrDefault(m => m.Id == current)
				?? throw new KeyNotFoundException($"Mailbox {current} is not known locally.");
			segments.Add(mailbox.Name);

			if (mailbox.ParentId is not Guid parentId)
			{
				break;
			}

			current = parentId;
		}

		segments.Reverse();
		return string.Join(separator, segments);
	}
}
