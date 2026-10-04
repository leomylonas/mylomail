using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MyloMail.Api.Contracts;
using MyloMail.Api.Hubs;
using MyloMail.Api.Persistence;

namespace MyloMail.Api.Controllers;

/// <summary>
/// Shell-wide settings that are not per-account (§13 Epic 11): a window reads the global default
/// panel layout and last-known bounds once at open, and writes back the same singleton row —
/// never a per-window value — when the user resizes or moves it (§12: global persisted default,
/// live per-window state does not sync elsewhere). Theme/close-behaviour/mailto-prompt have no
/// such carve-out, so those three broadcast <see cref="IHubEvents.ShellSettingsChangedAsync"/>
/// per Epic 10's "all actions reflected live across all open windows."
/// </summary>
[ApiController]
[Route("shell-settings")]
public class AppSettingsController(MyloMailDbContext context, IHubEvents events) : ControllerBase
{
	[HttpGet]
	public async Task<ActionResult<ShellSettingsDto>> Get(CancellationToken ct)
	{
		var settings = await context.AppSettings.SingleOrDefaultAsync(ct);
		return Ok(
			new ShellSettingsDto(
				settings?.PanelLayout,
				settings?.WindowBoundsJson,
				settings?.MailtoPromptDismissed ?? false,
				settings?.CloseBehavior ?? Domain.CloseBehavior.QuitApp,
				settings?.Theme ?? Domain.ThemePreference.System
			)
		);
	}

	[HttpPut("close-behavior")]
	public async Task<IActionResult> PutCloseBehavior(
		UpdateCloseBehaviorRequest request,
		CancellationToken ct
	)
	{
		var settings = await GetOrCreateAsync(ct);
		settings.CloseBehavior = request.CloseBehavior;
		await context.SaveChangesAsync(ct);
		await events.ShellSettingsChangedAsync();
		return NoContent();
	}

	[HttpPut("theme")]
	public async Task<IActionResult> PutTheme(UpdateThemeRequest request, CancellationToken ct)
	{
		var settings = await GetOrCreateAsync(ct);
		settings.Theme = request.Theme;
		await context.SaveChangesAsync(ct);
		await events.ShellSettingsChangedAsync();
		return NoContent();
	}

	[HttpPut("panel-layout")]
	public async Task<IActionResult> PutPanelLayout(
		UpdatePanelLayoutRequest request,
		CancellationToken ct
	)
	{
		var settings = await GetOrCreateAsync(ct);
		settings.PanelLayout = request.PanelLayout;
		await context.SaveChangesAsync(ct);
		return NoContent();
	}

	[HttpPut("window-bounds")]
	public async Task<IActionResult> PutWindowBounds(
		UpdateWindowBoundsRequest request,
		CancellationToken ct
	)
	{
		var settings = await GetOrCreateAsync(ct);
		settings.WindowBoundsJson = request.WindowBoundsJson;
		await context.SaveChangesAsync(ct);
		return NoContent();
	}

	/// <summary>
	/// The folder to open on launch. Like panel layout it is a read-once-at-open default that
	/// is not announced: a window that is already open keeps its own live selection. Resolved
	/// against current topology here, so a removed mailbox or disabled account reads as null.
	/// </summary>
	[HttpGet("last-viewed-mailbox")]
	public async Task<ActionResult<LastViewedMailboxDto>> GetLastViewedMailbox(
		CancellationToken ct
	)
	{
		var mailboxId = (await context.AppSettings.SingleOrDefaultAsync(ct))?.LastViewedMailboxId;
		if (mailboxId is null)
			return Ok(new LastViewedMailboxDto(null, null));

		var accountId = await context
			.Mailboxes.Where(m =>
				m.Id == mailboxId && context.Accounts.Any(a => a.Id == m.AccountId && a.IsEnabled)
			)
			.Select(m => (Guid?)m.AccountId)
			.SingleOrDefaultAsync(ct);
		return Ok(
			accountId is null
				? new LastViewedMailboxDto(null, null)
				: new LastViewedMailboxDto(accountId, mailboxId)
		);
	}

	/// <summary>
	/// Remembers the folder a plain shell window selected. Any such window writes it, so with
	/// several open the last click wins; it is never announced, because per-window live
	/// selection stays per-window. An unknown mailbox is rejected rather than stored.
	/// </summary>
	[HttpPut("last-viewed-mailbox")]
	public async Task<IActionResult> PutLastViewedMailbox(
		UpdateLastViewedMailboxRequest request,
		CancellationToken ct
	)
	{
		if (!await context.Mailboxes.AnyAsync(m => m.Id == request.MailboxId, ct))
			return NotFound();

		var settings = await GetOrCreateAsync(ct);
		if (settings.LastViewedMailboxId == request.MailboxId)
			return NoContent();

		settings.LastViewedMailboxId = request.MailboxId;
		await context.SaveChangesAsync(ct);
		return NoContent();
	}

	[HttpPut("mailto-prompt-dismissed")]
	public async Task<IActionResult> PutMailtoPromptDismissed(
		UpdateMailtoPromptDismissedRequest request,
		CancellationToken ct
	)
	{
		var settings = await GetOrCreateAsync(ct);
		settings.MailtoPromptDismissed = request.Dismissed;
		await context.SaveChangesAsync(ct);
		await events.ShellSettingsChangedAsync();
		return NoContent();
	}

	private async Task<Domain.AppSettings> GetOrCreateAsync(CancellationToken ct)
	{
		var settings = await context.AppSettings.SingleOrDefaultAsync(ct);
		if (settings is not null)
			return settings;

		settings = new Domain.AppSettings();
		context.AppSettings.Add(settings);
		return settings;
	}
}
