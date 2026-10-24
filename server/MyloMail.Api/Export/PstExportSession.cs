using System.Text.Json;
using MyloMail.Api.Domain;
using OfficeIMO.Email;
using OfficeIMO.Email.Store;

namespace MyloMail.Api.Export;

/// <summary>
/// Writes an export into one Unicode PST, resumably: the library keeps a durable checkpoint, and
/// what this export needs to carry across a restart (which PST folder each mailbox became, and how
/// far through the manifest the checkpoint is) is committed inside that same checkpoint. After a
/// crash the checkpoint is therefore the truth about the file; the database position is only a
/// hint, and is replaced by it.
/// </summary>
internal sealed class PstExportSession : IDisposable
{
	private readonly EmailStorePstWriter writer;

	private PstExportSession(EmailStorePstWriter writer, PstExportState state)
	{
		this.writer = writer;
		State = state;
	}

	public PstExportState State { get; private set; }

	public static string CheckpointPath(string pstPath) => pstPath + ".mylomail-export";

	/// <summary>
	/// Resumes the writer from its checkpoint, or starts a new PST with one folder per chosen mailbox
	/// (and the unchosen ancestors that hold them, empty). Null when there is no checkpoint but the
	/// PST already exists: it was finished, and only the database did not hear of it.
	/// </summary>
	public static PstExportSession? Open(
		string pstPath,
		string displayName,
		IReadOnlyList<Mailbox> mailboxes,
		IReadOnlyCollection<Guid> chosen
	)
	{
		var checkpoint = CheckpointPath(pstPath);
		if (File.Exists(checkpoint))
		{
			var resumed = EmailStorePstWriter.Resume(checkpoint, out var bytes);
			return new PstExportSession(resumed, JsonSerializer.Deserialize<PstExportState>(bytes)!);
		}

		if (File.Exists(pstPath))
		{
			return null;
		}

		Directory.CreateDirectory(Path.GetDirectoryName(pstPath)!);
		var writer = EmailStorePstWriter.Create(
			pstPath,
			new EmailStorePstWriterOptions(displayName: displayName, checkpointPath: checkpoint, checkpointIntervalItems: int.MaxValue)
		);
		var session = new PstExportSession(writer, new PstExportState(CreateFolders(writer, mailboxes, chosen), 0, 0, 0));
		session.Checkpoint(0, 0, 0);
		return session;
	}

	/// <summary>Whether a PST under this path was started and not finished.</summary>
	public static bool IsInProgress(string pstPath) => File.Exists(CheckpointPath(pstPath));

	/// <summary>Removes what an export that will not be finished left behind.</summary>
	public static void Discard(string pstPath)
	{
		if (File.Exists(CheckpointPath(pstPath)))
		{
			EmailStorePstWriter.DeleteCheckpoint(CheckpointPath(pstPath));
		}
	}

	/// <exception cref="InvalidOperationException">The mailbox is not one this export has a folder for.</exception>
	public void AddMessage(Guid mailboxId, byte[] raw, bool isRead, bool isFlagged)
	{
		if (!State.Folders.TryGetValue(mailboxId, out var folderId))
		{
			throw new InvalidOperationException("The mailbox is not part of this export.");
		}

		var document = EmailDocument.Load(raw);
		// The MIME says nothing of either; they are the account's own state, which Outlook keeps as
		// the read flag and a follow-up flag.
		document.MessageMetadata.IsRead = isRead;
		if (isFlagged)
		{
			document.MessageMetadata.FollowUp.Status = OutlookFollowUpStatus.Flagged;
			document.MessageMetadata.FollowUp.Request = "Follow up";
			document.MessageMetadata.FollowUp.Icon = OutlookFollowUpIcon.Red;
		}

		writer.AddItem(folderId, document);
	}

	public void Checkpoint(int resumeToken, int written, int skipped)
	{
		State = State with { ResumeToken = resumeToken, Written = written, Skipped = skipped };
		writer.Checkpoint(JsonSerializer.SerializeToUtf8Bytes(State));
	}

	public void Complete() => writer.Complete();

	public void Dispose() => writer.Dispose();

	private static Dictionary<Guid, string> CreateFolders(
		EmailStorePstWriter writer,
		IReadOnlyList<Mailbox> mailboxes,
		IReadOnlyCollection<Guid> chosen
	)
	{
		var byId = mailboxes.ToDictionary(m => m.Id);
		var needed = new HashSet<Guid>();
		foreach (var id in chosen)
		{
			for (Guid? current = id; current is Guid walk && byId.TryGetValue(walk, out var mailbox); current = mailbox.ParentId)
			{
				if (!needed.Add(walk))
				{
					break;
				}
			}
		}

		var created = new Dictionary<Guid, string>();
		var usedKinds = new HashSet<EmailStoreSpecialFolderKind>();

		string Create(Mailbox mailbox)
		{
			if (created.TryGetValue(mailbox.Id, out var known))
			{
				return known;
			}

			string? parent = mailbox.ParentId is Guid parentId && byId.TryGetValue(parentId, out var p) ? Create(p) : null;
			var name = ExportNames.Segment(mailbox.Name, "Folder");
			var kind = parent is null ? KindFor(mailbox.EffectiveSpecialUse) : EmailStoreSpecialFolderKind.Unknown;
			var id =
				kind != EmailStoreSpecialFolderKind.Unknown && EmailStorePstWriter.CanAssignSpecialFolderKind(kind) && usedKinds.Add(kind)
					? writer.AddFolder(name, kind)
					: writer.AddFolder(name, parent);
			return created[mailbox.Id] = id;
		}

		foreach (var mailbox in mailboxes.Where(m => needed.Contains(m.Id)).OrderBy(m => m.Name, StringComparer.Ordinal).ThenBy(m => m.Id))
		{
			Create(mailbox);
		}

		return created;
	}

	private static EmailStoreSpecialFolderKind KindFor(SpecialUse use) =>
		use switch
		{
			SpecialUse.Inbox => EmailStoreSpecialFolderKind.Inbox,
			SpecialUse.Sent => EmailStoreSpecialFolderKind.SentItems,
			SpecialUse.Drafts => EmailStoreSpecialFolderKind.Drafts,
			SpecialUse.Trash => EmailStoreSpecialFolderKind.DeletedItems,
			SpecialUse.Junk => EmailStoreSpecialFolderKind.JunkEmail,
			_ => EmailStoreSpecialFolderKind.Unknown,
		};
}

/// <summary>What is committed inside the PST writer's checkpoint.</summary>
internal sealed record PstExportState(Dictionary<Guid, string> Folders, int ResumeToken, int Written, int Skipped);
