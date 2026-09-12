import { describe, expect, it } from "vitest";
import {
	parseMessageDrag,
	serialiseMessageDrag,
} from "@mylomail/renderer/Lib/DragTypes";

describe("message drag payload", () => {
	it("round-trips the account, source mailbox, and selected messages", () => {
		const payload = {
			accountId: "account-a",
			sourceMailboxId: "inbox",
			messageIds: ["one", "two"],
		};

		expect(parseMessageDrag(serialiseMessageDrag(payload))).toEqual(payload);
	});

	it("rejects unscoped or empty payloads", () => {
		expect(parseMessageDrag("one,two")).toBeNull();
		expect(
			parseMessageDrag(
				JSON.stringify({
					accountId: "account-a",
					sourceMailboxId: "inbox",
					messageIds: [],
				}),
			),
		).toBeNull();
	});
});
