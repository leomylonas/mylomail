import { OutboxStatus } from "@mylomail/shared-types/SignalR/MyloMail.Api.Domain";

export interface SentState {
	cancelled: boolean;
	scheduledFor?: Date;
	status?: OutboxStatus;
	lastError?: string;
	reconcilingSince?: Date;
	/**
	 * An undo attempt lost its compare-and-swap against the send worker (§15) — the worker had
	 * already claimed the item, so cancelling was never going to succeed. Distinct from
	 * `cancelled: false`'s default (no attempt made yet): without this, clicking "Undo send"
	 * after the worker wins does nothing visible at all until the next status announcement,
	 * which can be seconds away.
	 */
	undoRejected?: boolean;
}

/**
 * Mirrors `SendReconciler.ReconciliationWindow` server-side — how long an
 * `AmbiguousOutcome` stays genuinely open before the server's own reconciliation pass would
 * have resolved or expired it. Duplicated rather than sent over the wire because it is a fixed
 * constant, not per-account configuration (unlike, say, `UndoSendDelaySeconds`).
 */
const RECONCILIATION_WINDOW_MS = 10 * 60 * 1000;

export interface SentDisplay {
	message: string;
	/** Whether the message text represents a failure worth an error style/`role="alert"`. */
	failed: boolean;
	/** Whether the outcome is still open enough that "Undo send" makes sense. */
	canUndo: boolean;
}

/**
 * Turns the compose window's local `Sent` state into what to show. Pulled out of `Compose.tsx`
 * so the mapping from a live `OutboxStatusChanged` update to on-screen text is unit-testable
 * without a React/SignalR harness this repo doesn't otherwise have (§7, §15).
 */
export function describeSentState(
	sent: SentState,
	now: Date = new Date(),
): SentDisplay {
	if (sent.cancelled || sent.status === OutboxStatus.Cancelled) {
		return {
			message: "Sending cancelled. Your message was not sent.",
			failed: false,
			canUndo: false,
		};
	}
	if (sent.status === OutboxStatus.Sent) {
		return { message: "Sent.", failed: false, canUndo: false };
	}
	if (sent.status === OutboxStatus.Failed) {
		return {
			message: sent.lastError ?? "This message could not be sent.",
			failed: true,
			canUndo: false,
		};
	}
	if (sent.status === OutboxStatus.AmbiguousOutcome) {
		// `lastError` alone can't distinguish "seconds-old raw exception from SendExecutor's
		// catch-all" from "SendReconciler's final verdict once its ReconciliationWindow
		// expires" — both populate the same field. `reconcilingSince` can: once the window has
		// genuinely elapsed, SendReconciler either resolved the item away from AmbiguousOutcome
		// entirely or is holding it exactly because reconciliation itself failed, so a raw
		// error surviving past the window is worth surfacing. Still never a hard failure
		// (§6, §15: an ambiguous/thrown outcome is not evidence of failure) — only whether to
		// keep the neutral in-window message or start showing the reconciler's own text.
		const expired =
			sent.reconcilingSince !== undefined &&
			now.getTime() - sent.reconcilingSince.getTime() >=
				RECONCILIATION_WINDOW_MS;
		return {
			message: expired
				? (sent.lastError ?? "This message's delivery could not be confirmed.")
				: "Confirming this was sent…",
			failed: false,
			canUndo: false,
		};
	}
	if (sent.undoRejected) {
		return {
			message: "Too late to undo — this message is already being sent.",
			failed: false,
			canUndo: false,
		};
	}
	return {
		message: sent.scheduledFor
			? `Scheduled for ${sent.scheduledFor.toLocaleString()}.`
			: "Sending…",
		failed: false,
		canUndo: true,
	};
}
