using MyloMail.Api.Domain;

namespace MyloMail.Api.Content;

/// <summary>Whether a sender's remote content (images, tracking pixels) may load without asking (§13 Epic 5).</summary>
public static class RemoteContentDecider
{
	/// <summary>Exact sender intent is more specific than a domain-wide rule. Unmatched mail stays blocked.</summary>
	public static RemoteContentDecision Decide(IReadOnlyCollection<RemoteContentRule> rules, string? senderAddress)
	{
		if (string.IsNullOrWhiteSpace(senderAddress))
		{
			return RemoteContentDecision.Default;
		}

		var sender = senderAddress.Trim().ToLowerInvariant();
		var senderRule = rules.FirstOrDefault(rule => rule.Scope == RemoteContentRuleScope.Sender && rule.Value == sender);
		if (senderRule is not null)
		{
			return Of(senderRule);
		}

		var separator = sender.LastIndexOf('@');
		if (separator < 0 || separator == sender.Length - 1)
		{
			return RemoteContentDecision.Default;
		}

		var domain = sender[(separator + 1)..];
		var domainRule = rules.FirstOrDefault(rule => rule.Scope == RemoteContentRuleScope.Domain && rule.Value == domain);
		return domainRule is null ? RemoteContentDecision.Default : Of(domainRule);
	}

	/// <summary>Whether remote content loads for this view: a rule decides, otherwise the reader's own request for this view.</summary>
	public static bool Allows(RemoteContentDecision decision, bool readerAskedForThisView) =>
		decision switch
		{
			RemoteContentDecision.Allow => true,
			RemoteContentDecision.Block => false,
			_ => readerAskedForThisView,
		};

	private static RemoteContentDecision Of(RemoteContentRule rule) =>
		rule.Decision == RemoteContentRuleDecision.Allow ? RemoteContentDecision.Allow : RemoteContentDecision.Block;
}
