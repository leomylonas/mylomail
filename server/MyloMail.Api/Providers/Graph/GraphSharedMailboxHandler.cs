using System.Net;
using System.Text.Json;
using MyloMail.Api.Errors;

namespace MyloMail.Api.Providers.Graph;

/// <summary>
/// Pipeline stage installed only for a shared-mailbox account (<see cref="GraphMailbox.Handlers"/>).
/// It retargets <c>/me</c> requests to the shared mailbox and turns Exchange's permission denial
/// into a structured problem instead of letting the raw <c>ApiException</c> through.
/// </summary>
/// <remarks>
/// A 403 on a shared mailbox means the signed-in user lacks Full Access (reads, drafts,
/// folders), lacks Send As / Send on Behalf (<c>ErrorSendAsDenied</c> on send), or the
/// application registration lacks consent for the delegated <c>*.Shared</c> permissions.
/// None is transient and none is cured by signing in again, so it is reported as a provider
/// rejection carried by <see cref="ProviderAuthenticationException"/> — the same shape CalDAV
/// uses for a denied calendar — which the existing callers already pause an account on without
/// a retry loop.
/// </remarks>
internal sealed class GraphSharedMailboxHandler(GraphMailbox mailbox) : DelegatingHandler
{
	protected override async Task<HttpResponseMessage> SendAsync(
		HttpRequestMessage request,
		CancellationToken ct
	)
	{
		if (request.RequestUri is { } uri)
		{
			request.RequestUri = mailbox.Retarget(uri);
		}

		var response = await base.SendAsync(request, ct);
		if (response.StatusCode != HttpStatusCode.Forbidden)
		{
			return response;
		}

		using (response)
		{
			var code = await ErrorCodeAsync(response, ct);
			throw new ProviderAuthenticationException(DeniedProblem(code));
		}
	}

	internal static MutationProblemDetails DeniedProblem(string? providerCode)
	{
		var sendDenied = providerCode?.StartsWith("ErrorSend", StringComparison.OrdinalIgnoreCase) == true;
		return new MutationProblemDetails
		{
			Category = ErrorCategory.ProviderRejected,
			Title = sendDenied ? "Send permission denied" : "Shared mailbox access denied",
			Detail = sendDenied
				? "Microsoft 365 refused to send as this shared mailbox. Ask an administrator to grant "
					+ "your account Send As or Send on Behalf on it, then try again."
				: "Microsoft 365 denied access to this shared mailbox. Your account needs Full Access "
					+ "to it, and MyloMail's app registration needs the delegated Mail.ReadWrite.Shared, "
					+ "Mail.Send.Shared and Calendars.ReadWrite.Shared permissions consented.",
			Status = StatusCodes.Status403Forbidden,
			ProviderCode = providerCode ?? "403",
		};
	}

	private static async Task<string?> ErrorCodeAsync(HttpResponseMessage response, CancellationToken ct)
	{
		try
		{
			var body = await response.Content.ReadAsStringAsync(ct);
			using var document = JsonDocument.Parse(body);
			return document.RootElement.TryGetProperty("error", out var error)
				&& error.TryGetProperty("code", out var code)
				&& code.ValueKind == JsonValueKind.String
					? code.GetString()
					: null;
		}
		catch (JsonException)
		{
			return null;
		}
	}
}
