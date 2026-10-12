using MyloMail.Api.Content;
using MyloMail.Api.Domain;
using Xunit;

namespace MyloMail.Api.Tests.Content;

public sealed class RemoteContentDeciderTests
{
	private static RemoteContentRule Rule(RemoteContentRuleScope scope, RemoteContentRuleDecision decision, string value) =>
		new() { Id = Guid.NewGuid(), Scope = scope, Decision = decision, Value = value };

	[Fact]
	public void An_exact_sender_allow_overrides_a_domain_block()
	{
		var rules = new[]
		{
			Rule(RemoteContentRuleScope.Domain, RemoteContentRuleDecision.Block, "example.org"),
			Rule(RemoteContentRuleScope.Sender, RemoteContentRuleDecision.Allow, "safe@example.org"),
		};

		Assert.Equal(RemoteContentDecision.Allow, RemoteContentDecider.Decide(rules, "SAFE@example.org"));
	}

	[Fact]
	public void An_exact_sender_block_overrides_a_domain_allow()
	{
		var rules = new[]
		{
			Rule(RemoteContentRuleScope.Domain, RemoteContentRuleDecision.Allow, "example.org"),
			Rule(RemoteContentRuleScope.Sender, RemoteContentRuleDecision.Block, "tracker@example.org"),
		};

		Assert.Equal(RemoteContentDecision.Block, RemoteContentDecider.Decide(rules, "tracker@example.org"));
	}

	[Fact]
	public void A_domain_rule_applies_to_every_sender_in_it()
	{
		var rules = new[] { Rule(RemoteContentRuleScope.Domain, RemoteContentRuleDecision.Allow, "example.org") };

		Assert.Equal(RemoteContentDecision.Allow, RemoteContentDecider.Decide(rules, "anyone@Example.org"));
	}

	[Theory]
	[InlineData("unknown@example.org")]
	[InlineData(null)]
	[InlineData("")]
	[InlineData("no-at-sign")]
	[InlineData("trailing@")]
	public void An_unmatched_or_unusable_sender_stays_on_the_default(string? sender)
	{
		Assert.Equal(RemoteContentDecision.Default, RemoteContentDecider.Decide([], sender));
	}

	[Theory]
	[InlineData(RemoteContentDecision.Allow, false, true)]
	[InlineData(RemoteContentDecision.Allow, true, true)]
	[InlineData(RemoteContentDecision.Block, true, false)]
	[InlineData(RemoteContentDecision.Block, false, false)]
	[InlineData(RemoteContentDecision.Default, true, true)]
	[InlineData(RemoteContentDecision.Default, false, false)]
	public void A_rule_decides_and_otherwise_the_readers_request_for_this_view_does(
		RemoteContentDecision decision,
		bool asked,
		bool expected
	) => Assert.Equal(expected, RemoteContentDecider.Allows(decision, asked));
}
