using Xunit;

namespace MyloMail.Api.Tests.Providers;

internal static class LiveProviderTestCollections
{
	public const string Gmail = "Gmail live provider";
	public const string Graph = "Graph live provider";

	/// <summary>
	/// Every live IMAP class works in the same account's INBOX, Archive and Trash. Run in parallel they
	/// move each other's messages and a different one failed on each run.
	/// </summary>
	public const string Imap = "IMAP live server";
}

[CollectionDefinition(LiveProviderTestCollections.Gmail, DisableParallelization = true)]
public sealed class GmailLiveProviderCollection;

[CollectionDefinition(LiveProviderTestCollections.Imap, DisableParallelization = true)]
public sealed class ImapLiveProviderCollection;

[CollectionDefinition(LiveProviderTestCollections.Graph, DisableParallelization = true)]
public sealed class GraphLiveProviderCollection;
