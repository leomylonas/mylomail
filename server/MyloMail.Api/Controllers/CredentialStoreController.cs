using Microsoft.AspNetCore.Mvc;
using MyloMail.Api.Contracts;
using MyloMail.Api.Credentials;

namespace MyloMail.Api.Controllers;

/// <summary>
/// Visibility into which credential store is actually in effect (§4, §8) — a
/// settings screen surfacing this is required so a user on a platform without a working
/// native store knows their credentials rest on the weaker fallback.
/// </summary>
[ApiController]
[Route("credential-store")]
public class CredentialStoreController(CredentialStoreSelector selector) : ControllerBase
{
	[HttpGet("status")]
	public ActionResult<CredentialStoreStatusDto> Get() =>
		Ok(new CredentialStoreStatusDto(selector.UsingNativeStore));

	/// <summary>
	/// The user's explicit "unlock" action: shows the desktop's unlock prompt for the native
	/// store. The accounts recover by themselves on their next attempt once it is readable.
	/// </summary>
	[HttpPost("unlock")]
	public async Task<IActionResult> Unlock(CancellationToken ct)
	{
		if (selector.UsingNativeStore) await NativeCredentialStore.UnlockAsync(ct);
		return NoContent();
	}
}
