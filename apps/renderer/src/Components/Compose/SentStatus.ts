import { OutboxStatus } from "@mylomail/shared-types/SignalR/MyloMail.Api.Domain";

export interface SentState {
	cancelled: boolean;
	scheduledFor?: Date;
	status?: OutboxStatus;
	lastError?: string;
}

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
export function describeSentState(sent: SentState): SentDisplay {
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
		// Deliberately ignores `lastError` here, unlike Failed: it is populated on the very
		// first AmbiguousOutcome announcement by SendExecutor's catch-all (the raw exception
		// message from whatever provider call failed), not only once SendReconciler's
		// ReconciliationWindow actually expires — the DTO carries no separate signal (e.g. a
		// ReconcilingSince timestamp) this UI could use to tell the two apart. Showing that
		// raw, possibly seconds-old exception text as a firm failure would misrepresent an
		// outcome that is still open for up to ReconciliationWindow (10 minutes) — the
		// invariant this whole status exists to protect (§6, §15: an ambiguous/thrown outcome
		// is not evidence of failure). A neutral, non-alert message is the safe choice either
		// way, until the server exposes something this can key off instead.
		return {
			message: "Confirming this was sent…",
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
