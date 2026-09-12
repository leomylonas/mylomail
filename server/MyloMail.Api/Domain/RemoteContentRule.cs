using Tapper;

namespace MyloMail.Api.Domain;

/// <summary>
/// A persisted request-layer decision for remote message content. Exact sender rules take
/// precedence over domain rules; without a matching allow rule content remains blocked.
/// </summary>
public class RemoteContentRule
{
	public Guid Id { get; set; }
	public RemoteContentRuleScope Scope { get; set; }
	public RemoteContentRuleDecision Decision { get; set; }
	public required string Value { get; set; }
	public DateTimeOffset CreatedAt { get; set; }
}

[TranspilationSource]
public enum RemoteContentRuleScope
{
	Sender,
	Domain,
}

[TranspilationSource]
public enum RemoteContentRuleDecision
{
	Allow,
	Block,
}
