using Microsoft.AspNetCore.Mvc;

namespace MyloMail.Api.Errors;

/// <summary>
/// RFC 7807 problem details plus the error taxonomy (§15). One error shape across REST and
/// SignalR: hub methods throw a <c>HubException</c> carrying this serialised, so the
/// renderer deserialises the same type from both transports.
/// </summary>
/// <remarks>
/// The inherited <c>Type</c>/<c>Title</c>/<c>Status</c>/<c>Detail</c> members carry the
/// human-readable, safe-to-show message. Never let a provider exception reach the UI
/// unmapped, and never invent a second error shape.
/// </remarks>
public class MutationProblemDetails : ProblemDetails
{
	public ErrorCategory Category { get; set; } = ErrorCategory.Unknown;

	/// <summary>Raw provider error code. Diagnostics only; not necessarily shown to the user.</summary>
	public string? ProviderCode { get; set; }
}
