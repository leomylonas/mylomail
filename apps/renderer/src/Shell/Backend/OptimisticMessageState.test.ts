import { QueryClient } from "@tanstack/react-query";
import { describe, expect, it } from "vitest";
import {
	acceptOptimisticMessages,
	hideOptimisticMessages,
	optimisticMessageIds,
	optimisticMessageIdsKey,
	optimisticMutationIds,
	restoreOptimisticMessages,
	settleOptimisticMutation,
	settleOptimisticMutations,
	type OptimisticMessageClaim,
} from "@mylomail/renderer/Shell/Backend/OptimisticMessageState";

const claim = (messageId: string, claimId: string): OptimisticMessageClaim => ({
	messageId,
	claimId: `local:${claimId}`,
});

describe("optimistic membership state", () => {
	const hiddenIn = (queryClient: QueryClient, mailboxId: string) =>
		optimisticMessageIds(
			queryClient.getQueryData(optimisticMessageIdsKey(mailboxId)),
		);

	it("keeps source claims independently correlated through terminal outcomes", () => {
		const queryClient = new QueryClient();
		const inbox = claim("one", "inbox");
		const archive = claim("one", "archive");
		hideOptimisticMessages(queryClient, "inbox", [inbox]);
		hideOptimisticMessages(queryClient, "archive", [archive]);
		acceptOptimisticMessages(
			queryClient,
			"inbox",
			[inbox],
			[{ messageId: "one", mutationItemId: "mutation-inbox" }],
		);
		acceptOptimisticMessages(
			queryClient,
			"archive",
			[archive],
			[{ messageId: "one", mutationItemId: "mutation-archive" }],
		);

		settleOptimisticMutation(queryClient, {
			messageId: "one",
			mutationItemId: "mutation-inbox",
		});

		expect(hiddenIn(queryClient, "inbox")).toEqual([]);
		expect(hiddenIn(queryClient, "archive")).toEqual(["one"]);
	});

	it("keeps accepted bulk items hidden while restoring rejected items", () => {
		const queryClient = new QueryClient();
		const accepted = claim("one", "accepted");
		const rejected = claim("two", "rejected");
		hideOptimisticMessages(queryClient, "inbox", [accepted, rejected]);

		acceptOptimisticMessages(
			queryClient,
			"inbox",
			[accepted, rejected],
			[{ messageId: "one", mutationItemId: "mutation-one" }],
		);

		expect(hiddenIn(queryClient, "inbox")).toEqual(["one"]);
	});

	it("does not resurrect an item that settles before its enqueue response", () => {
		const queryClient = new QueryClient();
		const pending = claim("one", "early");
		hideOptimisticMessages(queryClient, "inbox", [pending]);
		settleOptimisticMutation(queryClient, {
			messageId: "one",
			mutationItemId: "mutation-one",
		});

		acceptOptimisticMessages(
			queryClient,
			"inbox",
			[pending],
			[{ messageId: "one", mutationItemId: "mutation-one" }],
		);

		expect(hiddenIn(queryClient, "inbox")).toEqual([]);
	});

	it("restores only request-local claims when enqueueing fails", () => {
		const queryClient = new QueryClient();
		const first = claim("one", "first");
		const second = claim("one", "second");
		hideOptimisticMessages(queryClient, "inbox", [first, second]);

		restoreOptimisticMessages(queryClient, "inbox", [first]);

		expect(hiddenIn(queryClient, "inbox")).toEqual(["one"]);
	});

	it("releases only terminal durable claims recovered after reconnect", () => {
		const queryClient = new QueryClient();
		const terminal = claim("one", "terminal");
		const pending = claim("two", "pending");
		hideOptimisticMessages(queryClient, "inbox", [terminal, pending]);
		acceptOptimisticMessages(
			queryClient,
			"inbox",
			[terminal, pending],
			[
				{ messageId: "one", mutationItemId: "mutation-terminal" },
				{ messageId: "two", mutationItemId: "mutation-pending" },
			],
		);

		expect(optimisticMutationIds(queryClient)).toEqual([
			"mutation-terminal",
			"mutation-pending",
		]);
		settleOptimisticMutations(queryClient, ["mutation-terminal"]);

		expect(hiddenIn(queryClient, "inbox")).toEqual(["two"]);
		expect(optimisticMutationIds(queryClient)).toEqual(["mutation-pending"]);
	});
});
