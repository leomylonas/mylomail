using MyloMail.Api.Domain;
using Tapper;

namespace MyloMail.Api.Contracts;

/// <summary>
/// An account the provider has asked MyloMail to slow down for (§3). Informational only: the
/// wait and the retry happen on their own, so this exists for a quiet status-bar notice, never
/// for anything the user has to act on.
/// </summary>
/// <param name="AccountName">The account's display name.</param>
/// <param name="RetryAfterSeconds">Whole seconds until the account's jobs may run again.</param>
[TranspilationSource]
public record ThrottleStatusDto(Guid AccountId, string AccountName, ProviderType Provider, int RetryAfterSeconds);
