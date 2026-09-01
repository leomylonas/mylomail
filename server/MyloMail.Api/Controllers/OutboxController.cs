using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MyloMail.Api.Domain;
using MyloMail.Api.Persistence;

namespace MyloMail.Api.Controllers;

/// <summary>
/// Read-only outbox visibility for the shell process, which has no SignalR connection of its
/// own (§9) — Electron's main process talks to the backend over plain REST, the same way it
/// reads <c>/shell-settings</c>.
/// </summary>
[ApiController]
[Route("outbox")]
public class OutboxController(MyloMailDbContext context) : ControllerBase
{
	/// <summary>
	/// How many messages are still waiting to send — undo-send's delay and an arbitrary
	/// future schedule are the same mechanism (<see cref="OutboxItem"/>'s own remarks), both
	/// sitting in <see cref="OutboxStatus.Scheduled"/> until the worker claims them.
	/// </summary>
	/// <remarks>
	/// Backs the quit-confirmation prompt (§15): "quitting with pending scheduled messages
	/// prompts for confirmation" — a message still inside its undo-send window when the app
	/// quits is otherwise lost with no warning, since job storage is in-memory (§9) and this
	/// one has not yet reached the point startup reconciliation would re-enqueue it from.
	/// </remarks>
	[HttpGet("pending-count")]
	public async Task<ActionResult<int>> PendingCount(CancellationToken ct) =>
		Ok(await context.OutboxItems.CountAsync(o => o.Status == OutboxStatus.Scheduled, ct));
}
