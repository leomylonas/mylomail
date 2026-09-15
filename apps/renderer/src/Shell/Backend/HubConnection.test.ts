import { describe, expect, it, vi } from "vitest";
import { QueryClient } from "@tanstack/react-query";
import type { MessageSummaryDto } from "@mylomail/shared-types/SignalR/MyloMail.Api.Contracts";
import {
	dispatchNativeNotification,
	mergeServerKnownProjection,
	queryKeys,
	removeAccountCaches,
} from "@mylomail/renderer/Shell/Backend/HubConnection";
function summary(
	overrides: Partial<MessageSummaryDto> = {},
): MessageSummaryDto {
	return {
		id: "message",
		accountId: "account",
		subject: "Subject",
		snippet: "",
		from: [],
		receivedAt: "2026-01-01T00:00:00Z",
		isRead: false,
		isFlagged: false,
		hasNonInlineAttachments: false,
		threadMessageCount: 1,
		...overrides,
	};
}

describe("dispatchNativeNotification", () => {
	it("leaves a durable notification pending when no native bridge exists", () => {
		const markNotificationDelivered = vi.fn(async () => undefined);

		expect(() =>
			dispatchNativeNotification(
				{ markNotificationDelivered },
				{
					id: "notification",
					accountId: "account",
					title: "New message",
					body: "You have mail.",
				},
				undefined,
			),
		).not.toThrow();
		expect(markNotificationDelivered).not.toHaveBeenCalled();
	});
});

describe("mergeServerKnownProjection", () => {
	it("does not let an older mutation event erase an acquired snippet", () => {
		const merged = mergeServerKnownProjection(
			summary({ snippet: "Body-derived preview" }),
			summary({ snippet: "", isRead: true }),
		);

		expect(merged.snippet).toBe("Body-derived preview");
		expect(merged.isRead).toBe(true);
	});

	it("fills a blank list preview from a content event", () => {
		const merged = mergeServerKnownProjection(
			summary(),
			summary({ snippet: "Body-derived preview", isFlagged: true }),
		);

		expect(merged.snippet).toBe("Body-derived preview");
		expect(merged.isFlagged).toBe(true);
	});
});

describe("removeAccountCaches", () => {
	it("removes only a removed account's mailbox, message, body, and search state", () => {
		const queryClient = new QueryClient();
		queryClient.setQueryData(queryKeys.mailboxes("removed"), [
			{ id: "old-box" },
		]);
		queryClient.setQueryData(queryKeys.mailboxes("retained"), [
			{ id: "new-box" },
		]);
		queryClient.setQueryData(queryKeys.messages("old-box"), [
			summary({ id: "old-message", accountId: "removed" }),
		]);
		queryClient.setQueryData(queryKeys.messages("new-box"), [
			summary({ id: "new-message", accountId: "retained" }),
		]);
		queryClient.setQueryData(["body", "old-message"], { text: "old" });
		queryClient.setQueryData(["body", "new-message"], { text: "new" });
		queryClient.setQueryData(queryKeys.search("removed", "invoice", null), [
			summary({ id: "old-message", accountId: "removed" }),
		]);
		queryClient.setQueryData(queryKeys.search("retained", "invoice", null), [
			summary({ id: "new-message", accountId: "retained" }),
		]);

		removeAccountCaches(queryClient, "removed");

		expect(
			queryClient.getQueryData(queryKeys.mailboxes("removed")),
		).toBeUndefined();
		expect(
			queryClient.getQueryData(queryKeys.messages("old-box")),
		).toBeUndefined();
		expect(queryClient.getQueryData(["body", "old-message"])).toBeUndefined();
		expect(
			queryClient.getQueryData(queryKeys.search("removed", "invoice", null)),
		).toBeUndefined();
		expect(
			queryClient.getQueryData(queryKeys.mailboxes("retained")),
		).toBeDefined();
		expect(
			queryClient.getQueryData(queryKeys.messages("new-box")),
		).toBeDefined();
		expect(queryClient.getQueryData(["body", "new-message"])).toBeDefined();
		expect(
			queryClient.getQueryData(queryKeys.search("retained", "invoice", null)),
		).toBeDefined();
	});
});
