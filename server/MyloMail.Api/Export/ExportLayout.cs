using MyloMail.Api.Domain;

namespace MyloMail.Api.Export;

/// <summary>Where each mailbox goes on disk.</summary>
internal static class ExportLayout
{
	/// <summary>
	/// Every mailbox's path under <paramref name="root"/>. As a folder tree a mailbox is a directory
	/// inside its parent's. As mbox it is a file, and its subfolders sit in a sibling directory named
	/// as Thunderbird does: <c>Bills.mbox</c> beside <c>Bills.sbd/</c>. Two mailboxes whose names collapse
	/// to the same text under one parent are kept apart, in a fixed order, so a resumed export
	/// recreates the same paths.
	/// </summary>
	public static Dictionary<Guid, string> Build(IReadOnlyList<Mailbox> mailboxes, string root, ExportFormat format)
	{
		var byId = mailboxes.ToDictionary(m => m.Id);
		var paths = new Dictionary<Guid, string>();
		// Without the .mbox extension: what a mailbox's subfolder directory is named after.
		var stems = new Dictionary<Guid, string>();
		var takenUnder = new Dictionary<string, HashSet<string>>();

		string PathFor(Mailbox mailbox)
		{
			if (paths.TryGetValue(mailbox.Id, out var known))
			{
				return known;
			}

			string container;
			if (mailbox.ParentId is Guid parentId && byId.TryGetValue(parentId, out var parent))
			{
				PathFor(parent);
				container = format == ExportFormat.Mbox ? stems[parent.Id] + ".sbd" : paths[parent.Id];
			}
			else
			{
				container = root;
			}

			if (!takenUnder.TryGetValue(container, out var taken))
			{
				takenUnder[container] = taken = [];
			}

			var name = ExportNames.Unique(ExportNames.Segment(mailbox.Name, "Folder"), taken);
			var stem = Path.Combine(container, name);
			stems[mailbox.Id] = stem;
			return paths[mailbox.Id] = format == ExportFormat.Mbox ? stem + ".mbox" : stem;
		}

		// A parent is always placed before its children, and siblings in a fixed order.
		foreach (var mailbox in mailboxes.OrderBy(m => m.Name, StringComparer.Ordinal).ThenBy(m => m.Id))
		{
			PathFor(mailbox);
		}

		return paths;
	}
}
