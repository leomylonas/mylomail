namespace MyloMail.Api.Contracts;

/// <summary>
/// The shell-facing subset of <see cref="Domain.AppSettings"/> — window/layout state a window
/// reads once at open and writes back as a global default (§13 Epic 11).
/// </summary>
public record ShellSettingsDto(string? PanelLayout, string? WindowBoundsJson);

public record UpdatePanelLayoutRequest(string PanelLayout);

public record UpdateWindowBoundsRequest(string WindowBoundsJson);
