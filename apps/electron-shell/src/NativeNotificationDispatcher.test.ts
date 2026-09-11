import { describe, expect, it, vi } from "vitest";
import type { NotificationRequest } from "@mylomail/electron-shell/BackendConnection";
import { NativeNotificationDispatcher } from "@mylomail/electron-shell/NativeNotificationDispatcher";

const request = (id: string): NotificationRequest => ({
	id,
	accountId: "00000000-0000-0000-0000-000000000001",
	title: "New message",
	body: "A message arrived.",
});

describe("NativeNotificationDispatcher", () => {
	it("shows one native notification when several renderers relay the same durable event", () => {
		const showNative = vi.fn();
		const dispatcher = new NativeNotificationDispatcher(showNative);

		dispatcher.dispatch(request("notification-a"));
		dispatcher.dispatch(request("notification-a"));
		dispatcher.dispatch(request("notification-b"));

		expect(showNative.mock.calls.map(([shown]) => shown.id)).toEqual([
			"notification-a",
			"notification-b",
		]);
	});

	it("allows another renderer to retry when native dispatch fails", () => {
		const showNative = vi
			.fn<(request: NotificationRequest) => void>()
			.mockImplementationOnce(() => {
				throw new Error("native failure");
			});
		const dispatcher = new NativeNotificationDispatcher(showNative);

		expect(() => dispatcher.dispatch(request("notification-a"))).toThrow(
			"native failure",
		);
		dispatcher.dispatch(request("notification-a"));

		expect(showNative).toHaveBeenCalledTimes(2);
	});

	it("expires claims so process-lifetime memory stays bounded and later redispatch can retry", () => {
		let now = 100;
		const showNative = vi.fn();
		const dispatcher = new NativeNotificationDispatcher(
			showNative,
			() => now,
			50,
		);

		dispatcher.dispatch(request("notification-a"));
		now = 149;
		dispatcher.dispatch(request("notification-a"));
		now = 150;
		dispatcher.dispatch(request("notification-a"));

		expect(showNative).toHaveBeenCalledTimes(2);
	});
});
