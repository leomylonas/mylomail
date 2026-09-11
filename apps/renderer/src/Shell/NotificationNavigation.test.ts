import { describe, expect, it, vi } from "vitest";
import type { NotificationNavigationDto } from "@mylomail/shared-types/SignalR/MyloMail.Api.Contracts";
import { NotificationNavigationStatus } from "@mylomail/shared-types/SignalR/MyloMail.Api.Domain";
import {
	applyNotificationNavigation,
	waitForNotificationNavigation,
} from "@mylomail/renderer/Shell/NotificationNavigation";
import { createWindowStore } from "@mylomail/renderer/Shell/WindowScope/WindowStore";

const ready: NotificationNavigationDto = {
	status: NotificationNavigationStatus.Ready,
	accountId: "account-a",
	mailboxId: "mailbox-a",
	messageId: "message-a",
	subject: "Current subject",
	senderAddress: "sender@example.test",
};

describe("notification navigation", () => {
	it("keeps resolving staged mail until canonical navigation context is ready", async () => {
		const resolve = vi
			.fn<
				(notificationId: string) => Promise<NotificationNavigationDto | null>
			>()
			.mockResolvedValueOnce({
				status: NotificationNavigationStatus.Pending,
				accountId: "account-a",
				subject: "",
				senderAddress: "",
			})
			.mockResolvedValueOnce(ready);
		const pending = vi.fn();
		const wait = vi.fn().mockResolvedValue(undefined);

		const navigation = await waitForNotificationNavigation(
			resolve,
			"notification-a",
			new AbortController().signal,
			pending,
			wait,
		);

		expect(navigation).toEqual(ready);
		expect(resolve).toHaveBeenCalledTimes(2);
		expect(pending).toHaveBeenCalledOnce();
		expect(wait).toHaveBeenCalledWith(250, expect.any(AbortSignal));
	});

	it("stops retrying when the target window closes or another click supersedes it", async () => {
		const controller = new AbortController();
		const resolve = vi.fn().mockResolvedValue({
			status: NotificationNavigationStatus.Pending,
			accountId: "account-a",
			subject: "",
			senderAddress: "",
		});
		const wait = vi.fn().mockImplementation(() => {
			controller.abort();
			return Promise.resolve();
		});

		const navigation = await waitForNotificationNavigation(
			resolve,
			"notification-a",
			controller.signal,
			vi.fn(),
			wait,
		);

		expect(navigation).toBeNull();
		expect(resolve).toHaveBeenCalledOnce();
	});

	it("selects the owning account, mailbox, message, subject, and sender together", () => {
		const store = createWindowStore(null);

		expect(applyNotificationNavigation(store, ready)).toBe(true);

		expect(store.getState("selectedAccountId")).toBe("account-a");
		expect(store.getState("selectedMailboxId")).toBe("mailbox-a");
		expect(store.getState("selectedMessageId")).toBe("message-a");
		expect(store.getState("selectedMessageSubject")).toBe("Current subject");
		expect(store.getState("selectedMessageSenderAddress")).toBe(
			"sender@example.test",
		);
	});

	it("does not apply incomplete staged context as a real selection", () => {
		const store = createWindowStore(null);
		const pending: NotificationNavigationDto = {
			status: NotificationNavigationStatus.Pending,
			accountId: "account-a",
			subject: "",
			senderAddress: "",
		};

		expect(applyNotificationNavigation(store, pending)).toBe(false);
		expect(store.getState("selectedAccountId")).toBeNull();
	});
});
