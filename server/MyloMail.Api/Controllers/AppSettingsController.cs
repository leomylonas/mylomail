using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MyloMail.Api.Contracts;
using MyloMail.Api.Persistence;

namespace MyloMail.Api.Controllers;

/// <summary>
/// Shell-wide settings that are not per-account (§13 Epic 11): a window reads the global default
/// panel layout and last-known bounds once at open, and writes back the same singleton row —
/// never a per-window value — when the user resizes or moves it (§12: global persisted default,
/// live per-window state does not sync elsewhere).
/// </summary>
[ApiController]
[Route("shell-settings")]
public class AppSettingsController(MyloMailDbContext context) : ControllerBase
{
	[HttpGet]
	public async Task<ActionResult<ShellSettingsDto>> Get(CancellationToken ct)
	{
		var settings = await context.AppSettings.SingleOrDefaultAsync(ct);
		return Ok(
			new ShellSettingsDto(
				settings?.PanelLayout,
				settings?.WindowBoundsJson,
				settings?.MailtoPromptDismissed ?? false
			)
		);
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

	[HttpPut("mailto-prompt-dismissed")]
	public async Task<IActionResult> PutMailtoPromptDismissed(
		UpdateMailtoPromptDismissedRequest request,
		CancellationToken ct
	)
	{
		var settings = await GetOrCreateAsync(ct);
		settings.MailtoPromptDismissed = request.Dismissed;
		await context.SaveChangesAsync(ct);
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
