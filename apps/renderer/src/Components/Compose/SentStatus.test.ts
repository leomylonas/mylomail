import { describe, expect, it } from "vitest";
import { OutboxStatus } from "@mylomail/shared-types/SignalR/MyloMail.Api.Domain";
import { describeSentState } from "@mylomail/renderer/Components/Compose/SentStatus";

describe("describeSentState", () => {
	it("shows a live Sending state before any status announcement arrives", () => {
		const display = describeSentState({ cancelled: false });
		expect(display).toEqual({
			message: "Sending…",
			failed: false,
			canUndo: true,
		});
	});

	it("shows the scheduled time instead of Sending for a genuine future schedule", () => {
		const display = describeSentState({
			cancelled: false,
			scheduledFor: new Date("2026-01-01T09:00:00Z"),
		});
		expect(display.message).toContain("Scheduled for");
		expect(display.canUndo).toBe(true);
	});

	it("reports a confirmed Sent status and disables Undo", () => {
		const display = describeSentState({
			cancelled: false,
			status: OutboxStatus.Sent,
		});
		expect(display).toEqual({
			message: "Sent.",
			failed: false,
			canUndo: false,
		});
	});

	it("surfaces the server's LastError text on Failed, as a failure", () => {
		const display = describeSentState({
			cancelled: false,
			status: OutboxStatus.Failed,
			lastError: "the account needs reauthentication",
		});
		expect(display).toEqual({
			message: "the account needs reauthentication",
			failed: true,
			canUndo: false,
		});
	});

	it("falls back to a generic message on Failed with no LastError", () => {
		const display = describeSentState({
			cancelled: false,
			status: OutboxStatus.Failed,
		});
		expect(display.message).toBe("This message could not be sent.");
		expect(display.failed).toBe(true);
	});

	it("shows AmbiguousOutcome as still-confirming, never a failure, with no LastError yet", () => {
		const display = describeSentState({
			cancelled: false,
			status: OutboxStatus.AmbiguousOutcome,
		});
		expect(display).toEqual({
			message: "Confirming this was sent…",
			failed: false,
			canUndo: false,
		});
	});

	it("ignores LastError on AmbiguousOutcome, unlike Failed: SendExecutor sets it immediately with a raw provider exception, seconds into an outcome that stays open for up to SendReconciler.ReconciliationWindow (10 minutes) — showing it as a firm failure this early would misrepresent an outcome that is not yet known", () => {
		const display = describeSentState({
			cancelled: false,
			status: OutboxStatus.AmbiguousOutcome,
			lastError: "The operation has timed out.",
		});
		expect(display).toEqual({
			message: "Confirming this was sent…",
			failed: false,
			canUndo: false,
		});
	});

	it("still shows AmbiguousOutcome as confirming, ignoring LastError, while reconcilingSince is within the reconciliation window", () => {
		const now = new Date("2026-01-01T00:09:00.000Z");
		const display = describeSentState(
			{
				cancelled: false,
				status: OutboxStatus.AmbiguousOutcome,
				lastError: "The operation has timed out.",
				reconcilingSince: new Date("2026-01-01T00:00:00.000Z"),
			},
			now,
		);
		expect(display).toEqual({
			message: "Confirming this was sent…",
			failed: false,
			canUndo: false,
		});
	});

	it("surfaces LastError on AmbiguousOutcome once reconcilingSince has outlived the reconciliation window", () => {
		const now = new Date("2026-01-01T00:10:00.001Z");
		const display = describeSentState(
			{
				cancelled: false,
				status: OutboxStatus.AmbiguousOutcome,
				lastError: "The operation has timed out.",
				reconcilingSince: new Date("2026-01-01T00:00:00.000Z"),
			},
			now,
		);
		expect(display).toEqual({
			message: "The operation has timed out.",
			failed: false,
			canUndo: false,
		});
	});

	it("falls back to a neutral message once expired if LastError never arrived", () => {
		const now = new Date("2026-01-01T00:10:00.001Z");
		const display = describeSentState(
			{
				cancelled: false,
				status: OutboxStatus.AmbiguousOutcome,
				reconcilingSince: new Date("2026-01-01T00:00:00.000Z"),
			},
			now,
		);
		expect(display.message).toBe(
			"This message's delivery could not be confirmed.",
		);
		expect(display.failed).toBe(false);
	});

	it("prefers the locally-set cancelled flag over a not-yet-arrived status", () => {
		const display = describeSentState({ cancelled: true });
		expect(display.message).toBe(
			"Sending cancelled. Your message was not sent.",
		);
		expect(display.canUndo).toBe(false);
	});

	it("also recognizes a live Cancelled status announcement", () => {
		const display = describeSentState({
			cancelled: false,
			status: OutboxStatus.Cancelled,
		});
		expect(display.message).toBe(
			"Sending cancelled. Your message was not sent.",
		);
		expect(display.canUndo).toBe(false);
	});

	it("reports a lost undo attempt instead of silently doing nothing", () => {
		const display = describeSentState({
			cancelled: false,
			undoRejected: true,
		});
		expect(display.message).toBe(
			"Too late to undo — this message is already being sent.",
		);
		expect(display.failed).toBe(false);
		expect(display.canUndo).toBe(false);
	});

	it("prefers a real status announcement over a stale undoRejected flag", () => {
		const display = describeSentState({
			cancelled: false,
			undoRejected: true,
			status: OutboxStatus.Sent,
		});
		expect(display.message).toBe("Sent.");
	});
});
