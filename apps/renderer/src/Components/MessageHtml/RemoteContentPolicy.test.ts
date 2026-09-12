import type { RemoteContentRuleDto } from "@mylomail/shared-types/Api/Contracts/RemoteContentRuleDto";
import { RemoteContentRuleDecision } from "@mylomail/shared-types/Api/Domain/RemoteContentRuleDecision";
import { RemoteContentRuleScope } from "@mylomail/shared-types/Api/Domain/RemoteContentRuleScope";
import { describe, expect, it } from "vitest";
import { decideRemoteContent } from "@mylomail/renderer/Components/MessageHtml/RemoteContentPolicy";

const rule = (
	scope: RemoteContentRuleScope,
	decision: RemoteContentRuleDecision,
	value: string,
): RemoteContentRuleDto => ({
	id: crypto.randomUUID(),
	scope,
	decision,
	value,
});

describe("decideRemoteContent", () => {
	it("lets an exact sender allow override a domain block", () => {
		expect(
			decideRemoteContent(
				[
					rule(
						RemoteContentRuleScope.Domain,
						RemoteContentRuleDecision.Block,
						"example.org",
					),
					rule(
						RemoteContentRuleScope.Sender,
						RemoteContentRuleDecision.Allow,
						"safe@example.org",
					),
				],
				"SAFE@example.org",
			),
		).toBe("allow");
	});

	it("lets an exact sender block override a domain allow", () => {
		expect(
			decideRemoteContent(
				[
					rule(
						RemoteContentRuleScope.Domain,
						RemoteContentRuleDecision.Allow,
						"example.org",
					),
					rule(
						RemoteContentRuleScope.Sender,
						RemoteContentRuleDecision.Block,
						"tracker@example.org",
					),
				],
				"tracker@example.org",
			),
		).toBe("block");
	});

	it("blocks an unmatched sender by default", () => {
		expect(decideRemoteContent([], "unknown@example.org")).toBe("default");
	});
});
