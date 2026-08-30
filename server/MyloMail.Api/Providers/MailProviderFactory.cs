using MyloMail.Api.Domain;
using MyloMail.Api.Providers.Gmail;
using MyloMail.Api.Providers.Graph;

namespace MyloMail.Api.Providers;

/// <summary>
/// Resolves the implementation for a provider type, so that the orchestrator and hub layers
/// contain no provider-specific logic (§2).
/// </summary>
/// <remarks>
/// Providers are resolved optionally and the absence of one is reported, not constructed
/// around. Every provider ultimately needs the credential store from §4, whose production
/// registration is deliberately still absent; a factory that papered over that would turn a
/// missing credential source into a runtime authentication failure much further from its
/// cause.
/// </remarks>
public sealed class MailProviderFactory(IServiceProvider services) : IMailProviderFactory
{
	public IMailProvider For(ProviderType type)
	{
		var provider = type switch
		{
			ProviderType.Gmail => services.GetService<GmailMailProvider>() as IMailProvider,
			ProviderType.Microsoft365 => services.GetService<GraphMailProvider>(),
			ProviderType.Imap => services.GetService<Imap.ImapMailProvider>(),
			_ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown provider type."),
		};

		return provider
			?? throw new NotSupportedException(
				$"No {type} provider is registered. Provider registration waits on the credential "
					+ "store from §4, which is intentionally not wired up yet."
			);
	}
}
