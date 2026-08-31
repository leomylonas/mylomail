using MyloMail.Api.Errors;

namespace MyloMail.Api.Providers;

/// <summary>
/// The server's copy changed since the one being written was read (§1, §15).
/// </summary>
/// <remarks>
/// Surfaced as <see cref="ErrorCategory.Conflict"/> so the UI prompts for resolution rather
/// than overwriting. Detect-don't-merge is only enforceable because the revision is stored:
/// without it there is nothing to compare and every write silently wins.
/// </remarks>
public class ProviderConflictException(string message) : Exception(message);
