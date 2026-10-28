import type { QueryClient } from "@tanstack/react-query";

/**
 * A short, per-window record of what happened to messages in the list, kept so that a message
 * which comes back after being deleted can be explained rather than guessed at. Ids and event
 * names only: no subjects, addresses or bodies.
 */
export interface TraceEntry {
	at: number;
	label: string;
	detail?: string;
}

export interface Reappearance {
	at: number;
	messageId: string;
	/** The events leading up to it, oldest first. */
	trace: TraceEntry[];
}

const traceKey = ["activity-trace"] as const;
const reappearedKey = ["reappeared-messages"] as const;
const expectedKey = ["expected-returning-messages"] as const;
const traceLength = 400;
const reappearedKept = 10;

export function trace(
	queryClient: QueryClient,
	label: string,
	detail?: string,
): void {
	queryClient.setQueryData<TraceEntry[]>(traceKey, (current) =>
		[...(current ?? []), { at: Date.now(), label, detail }].slice(-traceLength),
	);
}

/** Messages that are meant to reappear (a failed or undone move), so that is not reported. */
export function expectReturn(
	queryClient: QueryClient,
	messageIds: readonly string[],
): void {
	queryClient.setQueryData<string[]>(expectedKey, (current) => [
		...new Set([...(current ?? []), ...messageIds]),
	]);
}

export function expectedReturns(queryClient: QueryClient): ReadonlySet<string> {
	return new Set(queryClient.getQueryData<string[]>(expectedKey) ?? []);
}

export function recordReappearance(
	queryClient: QueryClient,
	messageId: string,
): void {
	const events = (
		queryClient.getQueryData<TraceEntry[]>(traceKey) ?? []
	).filter(
		(entry) =>
			!entry.detail ||
			entry.detail.includes(messageId.slice(0, 8)) ||
			entry.label.startsWith("list:"),
	);
	queryClient.setQueryData<Reappearance[]>(reappearedKey, (current) =>
		[
			...(current ?? []),
			{ at: Date.now(), messageId, trace: events.slice(-80) },
		].slice(-reappearedKept),
	);
}

export function reappearances(queryClient: QueryClient): Reappearance[] {
	return queryClient.getQueryData<Reappearance[]>(reappearedKey) ?? [];
}

/** The trace as text for pasting into a report: seconds before the message came back, then the event. */
export function describeReappearance(entry: Reappearance): string {
	return [
		`Message ${entry.messageId} came back at ${new Date(entry.at).toISOString()}`,
		...entry.trace.map(
			(step) =>
				`${((step.at - entry.at) / 1000).toFixed(3)}s  ${step.label}${step.detail ? `  ${step.detail}` : ""}`,
		),
	].join("\n");
}
