import type { RemoteContentRuleDto } from "@mylomail/shared-types/Api/Contracts/RemoteContentRuleDto";
import { RemoteContentRuleDecision } from "@mylomail/shared-types/Api/Domain/RemoteContentRuleDecision";
import { RemoteContentRuleScope } from "@mylomail/shared-types/Api/Domain/RemoteContentRuleScope";

export type RemoteContentDecision = "allow" | "block" | "default";

/** Exact sender intent is more specific than a domain-wide rule. Unmatched mail stays blocked. */
export function decideRemoteContent(
	rules: readonly RemoteContentRuleDto[],
	senderAddress: string | undefined,
): RemoteContentDecision {
	if (!senderAddress) return "default";

	const sender = senderAddress.trim().toLowerCase();
	const senderRule = rules.find(
		(rule) =>
			rule.scope === RemoteContentRuleScope.Sender && rule.value === sender,
	);
	if (senderRule) return decisionOf(senderRule.decision);

	const separator = sender.lastIndexOf("@");
	if (separator < 0 || separator === sender.length - 1) return "default";
	const domain = sender.slice(separator + 1);
	const domainRule = rules.find(
		(rule) =>
			rule.scope === RemoteContentRuleScope.Domain && rule.value === domain,
	);
	return domainRule ? decisionOf(domainRule.decision) : "default";
}

function decisionOf(
	decision: RemoteContentRuleDecision,
): RemoteContentDecision {
	return decision === RemoteContentRuleDecision.Allow ? "allow" : "block";
}
