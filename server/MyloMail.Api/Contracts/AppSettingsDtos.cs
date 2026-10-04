using MyloMail.Api.Domain;

namespace MyloMail.Api.Contracts;

/// <summary>
/// The shell-facing subset of <see cref="Domain.AppSettings"/> — window/layout state a window
/// reads once at open and writes back as a global default (§13 Epic 11), plus the main
/// process's own close-behaviour policy (§8, §13 Epic 10: quit vs. minimise to tray).
/// </summary>
public record ShellSettingsDto(
	string? PanelLayout,
	string? WindowBoundsJson,
	bool MailtoPromptDismissed,
	CloseBehavior CloseBehavior,
	ThemePreference Theme
);

public record UpdatePanelLayoutRequest(string PanelLayout);

public record UpdateWindowBoundsRequest(string WindowBoundsJson);

/// <summary>
/// The remembered folder, resolved: both ids are null unless the mailbox still exists and its
/// account is enabled, so a caller never has to reconcile a stale id itself.
/// </summary>
public record LastViewedMailboxDto(Guid? AccountId, Guid? MailboxId);

public record UpdateLastViewedMailboxRequest(Guid MailboxId);

public record UpdateMailtoPromptDismissedRequest(bool Dismissed);

public record UpdateCloseBehaviorRequest(CloseBehavior CloseBehavior);

public record UpdateThemeRequest(ThemePreference Theme);

/// <summary>Whether this OS has a working native credential store, or the app fell back to
/// the weaker master-password-protected SQLite store (§4, §8).</summary>
public record CredentialStoreStatusDto(bool UsingNativeStore);
