export interface ProgressSample {
	at: number;
	value: number;
}

export type ProgressEstimate =
	| { kind: "measuring" }
	| { kind: "stalled"; sinceMs: number }
	| { kind: "moving"; perMinute: number; remainingMs: number | null };

/** Samples older than this no longer describe how fast it is going now. */
export const sampleWindowMs = 5 * 60_000;
const minimumSpanMs = 15_000;
const stalledAfterMs = 60_000;

/** Adds a sample, dropping those outside the window and any that repeat the previous reading's time. */
export function addSample(
	samples: readonly ProgressSample[],
	sample: ProgressSample,
): ProgressSample[] {
	const last = samples[samples.length - 1];
	const kept = samples.filter((s) => sample.at - s.at <= sampleWindowMs);
	return last && last.at === sample.at ? kept : [...kept, sample];
}

/**
 * How fast a count is climbing and when it will reach `total`, from readings taken over the last
 * few minutes. Needs a stretch of readings before it will say anything, and says "stalled" rather
 * than inventing a rate when nothing has moved.
 */
export function estimateProgress(
	samples: readonly ProgressSample[],
	total: number | null,
): ProgressEstimate {
	const first = samples[0];
	const last = samples[samples.length - 1];
	if (!first || !last || last.at - first.at < minimumSpanMs) {
		return { kind: "measuring" };
	}

	const gained = last.value - first.value;
	const spanMs = last.at - first.at;
	if (gained <= 0) {
		return spanMs >= stalledAfterMs
			? { kind: "stalled", sinceMs: spanMs }
			: { kind: "measuring" };
	}

	const perMinute = (gained / spanMs) * 60_000;
	const remaining = total === null ? null : Math.max(total - last.value, 0);
	return {
		kind: "moving",
		perMinute,
		remainingMs:
			remaining === null ? null : (remaining / (gained / spanMs)) | 0,
	};
}

export function formatDuration(ms: number): string {
	if (ms < 60_000) return "less than a minute";
	const minutes = Math.round(ms / 60_000);
	if (minutes < 60) return `${minutes} min`;
	const hours = Math.floor(minutes / 60);
	const rest = minutes % 60;
	if (hours < 24) return rest === 0 ? `${hours} h` : `${hours} h ${rest} min`;
	const days = Math.floor(hours / 24);
	return `${days} d ${hours % 24} h`;
}

export function describeEstimate(
	estimate: ProgressEstimate,
	unit: string,
): string {
	switch (estimate.kind) {
		case "measuring":
			return "Measuring speed…";
		case "stalled":
			return `No progress in the last ${formatDuration(estimate.sinceMs)}`;
		case "moving": {
			const rate =
				estimate.perMinute >= 10
					? Math.round(estimate.perMinute).toLocaleString()
					: estimate.perMinute.toFixed(1);
			return estimate.remainingMs === null
				? `About ${rate} ${unit}/min`
				: `About ${rate} ${unit}/min · roughly ${formatDuration(estimate.remainingMs)} left`;
		}
	}
}
