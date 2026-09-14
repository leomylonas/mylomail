using Xunit;

namespace MyloMail.Api.Tests.Providers;

internal static class LiveProviderTestCollections
{
	public const string Gmail = "Gmail live provider";
	public const string Graph = "Graph live provider";
}

[CollectionDefinition(LiveProviderTestCollections.Gmail, DisableParallelization = true)]
public sealed class GmailLiveProviderCollection;

[CollectionDefinition(LiveProviderTestCollections.Graph, DisableParallelization = true)]
public sealed class GraphLiveProviderCollection;
