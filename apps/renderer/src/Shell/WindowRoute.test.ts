import { describe, expect, it } from "vitest";
import { parseWindowRoute } from "@mylomail/renderer/Shell/WindowRoute";

// Regression for the popped-out message window silently losing its sender address: `AppShell`
// passed `subject` over the query string when opening `?message=...` but not `sender`, so
// `MessageWindow` (via this parser) never had anything to hand `ReadingPane`/`MessageHtml` — an
// already-trusted sender's mail would still be blocked and re-prompted there, contradicting the
// persisted remote-content allow list's whole point of not asking twice.
describe("parseWindowRoute", () => {
	it("carries the sender address for a message route", () => {
		const route = parseWindowRoute(
			"?message=m1&subject=Hello&sender=someone%40example.test",
		);
		expect(route).toEqual({
			kind: "message",
			messageId: "m1",
			subject: "Hello",
			senderAddress: "someone@example.test",
		});
	});

	it("leaves the sender address undefined when the query string omits it", () => {
		const route = parseWindowRoute("?message=m1&subject=Hello");
		expect(route).toEqual({
			kind: "message",
			messageId: "m1",
			subject: "Hello",
			senderAddress: undefined,
		});
	});

	it("still resolves the compose route", () => {
		expect(parseWindowRoute("?compose=d1&account=a1")).toEqual({
			kind: "compose",
			draftId: "d1",
			accountId: "a1",
		});
	});

	it("carries a notification click into a newly opened main window", () => {
		expect(
			parseWindowRoute("?notification=n1&account=a1&windowSlot=3"),
		).toEqual({
			kind: "shell",
			initialNotification: {
				notificationId: "n1",
				accountId: "a1",
			},
		});
	});

	it("falls back to the shell route with no recognised params", () => {
		expect(parseWindowRoute("")).toEqual({ kind: "shell" });
	});
});
