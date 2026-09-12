using MyloMail.Api.Domain;

namespace MyloMail.Api.Contracts;

public record RemoteContentRuleDto(
	Guid Id,
	RemoteContentRuleScope Scope,
	RemoteContentRuleDecision Decision,
	string Value
);

public record PutRemoteContentRuleRequest(
	RemoteContentRuleScope Scope,
	RemoteContentRuleDecision Decision,
	string Value
);
