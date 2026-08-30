using Microsoft.AspNetCore.Mvc;
using MyloMail.Api.Contracts;

namespace MyloMail.Api.Controllers;

/// <summary>
/// Liveness probe. Electron polls this before connecting the renderer's SignalR client (§9).
/// </summary>
/// <remarks>
/// This runs before the renderer exists, so it is deliberately dependency-free: it reports
/// that the process is up and serving, not that any subsystem is healthy.
/// </remarks>
[ApiController]
[Route("health")]
public class HealthController : ControllerBase
{
	[HttpGet]
	public ActionResult<HealthDto> Get() => Ok(new HealthDto(Status: "ok"));
}
