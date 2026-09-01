using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MyloMail.Api.Contracts;
using MyloMail.Api.Persistence;

namespace MyloMail.Api.Controllers;

/// <summary>
/// The persisted per-sender remote-content allow list (§13 Epic 5). Remote content stays
/// blocked by default for everyone not on this list — see
/// <see cref="Domain.TrustedRemoteContentSender"/> for why there is no separate block list.
/// </summary>
[ApiController]
[Route("remote-content/trusted-senders")]
public class RemoteContentController(MyloMailDbContext context) : ControllerBase
{
	[HttpGet]
	public async Task<ActionResult<IReadOnlyList<TrustedSenderDto>>> Get(CancellationToken ct) =>
		Ok(
			await context.TrustedRemoteContentSenders
				.OrderBy(x => x.Address)
				.Select(x => new TrustedSenderDto(x.Address))
				.ToListAsync(ct)
		);

	[HttpPost]
	public async Task<IActionResult> Trust(TrustSenderRequest request, CancellationToken ct)
	{
		var address = request.Address.Trim().ToLowerInvariant();
		if (address.Length == 0)
		{
			return BadRequest();
		}

		var exists = await context.TrustedRemoteContentSenders.AnyAsync(x => x.Address == address, ct);
		if (!exists)
		{
			context.TrustedRemoteContentSenders.Add(
				new Domain.TrustedRemoteContentSender { Id = Guid.NewGuid(), Address = address, CreatedAt = DateTimeOffset.UtcNow }
			);
			await context.SaveChangesAsync(ct);
		}

		return NoContent();
	}

	[HttpDelete("{address}")]
	public async Task<IActionResult> Untrust(string address, CancellationToken ct)
	{
		var normalized = address.Trim().ToLowerInvariant();
		var sender = await context.TrustedRemoteContentSenders.FirstOrDefaultAsync(x => x.Address == normalized, ct);
		if (sender is not null)
		{
			context.TrustedRemoteContentSenders.Remove(sender);
			await context.SaveChangesAsync(ct);
		}

		return NoContent();
	}
}
