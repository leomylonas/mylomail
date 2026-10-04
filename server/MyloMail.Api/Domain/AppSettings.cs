namespace MyloMail.Api.Domain;

/// <summary>
/// Single-row application settings (§9). Every field has a sensible default, so a missing
/// row is equivalent to all-defaults and no initialisation file is required.
/// </summary>
/// <remarks>
/// <c>DataDirectoryOverride</c> is deliberately absent: it lives in the bootstrap file,
/// because it must be resolved before this table can be located.
/// </remarks>
public class AppSettings
{
	/// <summary>Fixed — this table holds exactly one row.</summary>
	public int Id { get; set; } = 1;

	public CloseBehavior CloseBehavior { get; set; } = CloseBehavior.QuitApp;

	/// <summary>"Don't ask again" for the default-handler prompt.</summary>
	public bool MailtoPromptDismissed { get; set; }

	/// <summary>Global default panel sizes. Live per-window state is not stored here (§13).</summary>
	public string? PanelLayout { get; set; }

	/// <summary>Last-known size and position, inherited by newly opened windows.</summary>
	public string? WindowBoundsJson { get; set; }

	/// <summary>
	/// The mailbox the last plain shell window selected, so the next launch opens to it.
	/// A local UI preference like <c>Mailbox.IsCollapsed</c>, deliberately without a foreign
	/// key: mailboxes are replaced by topology reconciliation, and a stale id must resolve to
	/// "nothing remembered" when read (see <c>AppSettingsController.GetLastViewedMailbox</c>)
	/// instead of blocking the delete or being rewritten by it.
	/// </summary>
	public Guid? LastViewedMailboxId { get; set; }

	public ThemePreference Theme { get; set; } = ThemePreference.System;

	/// <summary>Sweeps orphaned temp attachments left by a crash.</summary>
	public bool AttachmentTempCleanupOnStartup { get; set; } = true;
	/// <summary>Durable completion marker for the restart-safe conversation-id upgrade.</summary>
	public int MessageThreadBackfillVersion { get; set; }
}

public enum CloseBehavior
{
	QuitApp,
	MinimizeToTray,
}

public enum ThemePreference
{
	System,
	Light,
	Dark,
}
