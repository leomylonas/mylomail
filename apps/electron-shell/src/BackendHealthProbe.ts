import type { ChildProcess } from "node:child_process";
import type { BackendLaunch } from "@mylomail/electron-shell/BackendSupervisor";

const portPrefix = "MYLOMAIL_PORT=";
const startupPrefix = "MYLOMAIL_STARTUP=";

/** A long startup phase the backend is working through, e.g. the pre-migration backup. */
export interface StartupProgress {
	phase: string;
	/** 0–1, or null when the phase is running but its length is unknown. */
	fraction: number | null;
}

export interface BackendHealthProbeOptions {
	/**
	 * Budget for the port announcement and the first healthy response. While the backend
	 * reports startup progress the budget restarts with every report, so a long backup or
	 * migration is not mistaken for a hung process; silence for this long is still a failure.
	 */
	timeoutMs?: number;
	/** Called for each progress line the backend prints before it announces its port. */
	onProgress?: (progress: StartupProgress) => void;
	/** Gap between health attempts once the port is known. */
	pollIntervalMs?: number;
	fetchHealth?: (url: string, token: string) => Promise<{ ok: boolean }>;
	delay?: (ms: number) => Promise<void>;
}

/**
 * Waits for the backend's loopback port announcement, then polls `/health` with this
 * launch's bearer token until it answers (§9).
 *
 * The port is returned rather than discarded: it is assigned by the OS, so it is the only
 * way the renderer can address the backend at all, and nothing else learns it.
 */
export async function waitForBackendHealth(
	backend: BackendLaunch,
	options: BackendHealthProbeOptions = {},
): Promise<number> {
	const timeoutMs = options.timeoutMs ?? 30_000;
	const port = await readPort(backend.child, timeoutMs, options.onProgress);
	await pollForHealth(
		`http://127.0.0.1:${port}/health`,
		backend.launchToken,
		Date.now() + timeoutMs,
		options,
	);
	return port;
}

/**
 * Polls a known attached backend rather than waiting for a child-process port announcement.
 * Attach mode still proves both reachability and possession of the shared launch token before
 * any renderer exists.
 */
export async function waitForBackendOriginHealth(
	origin: string,
	launchToken: string,
	options: BackendHealthProbeOptions = {},
): Promise<void> {
	await pollForHealth(
		`${origin}/health`,
		launchToken,
		Date.now() + (options.timeoutMs ?? 30_000),
		options,
	);
}

async function pollForHealth(
	healthUrl: string,
	launchToken: string,
	deadline: number,
	options: BackendHealthProbeOptions,
): Promise<void> {
	const pollIntervalMs = options.pollIntervalMs ?? 100;
	const fetchHealth = options.fetchHealth ?? defaultFetchHealth;
	const delay =
		options.delay ??
		((ms: number) => new Promise<void>((resolve) => setTimeout(resolve, ms)));
	let lastFailure: unknown;
	while (Date.now() < deadline) {
		try {
			const response = await fetchHealth(healthUrl, launchToken);
			if (response.ok) return;
			lastFailure = new Error(`health responded ${JSON.stringify(response)}`);
		} catch (error) {
			lastFailure = error;
		}
		await delay(pollIntervalMs);
	}

	throw new Error(
		`Backend did not become healthy within ${options.timeoutMs ?? 30_000}ms${
			lastFailure ? ` (last failure: ${String(lastFailure)})` : ""
		}.`,
	);
}

async function defaultFetchHealth(
	url: string,
	token: string,
): Promise<{ ok: boolean }> {
	const response = await fetch(url, {
		headers: { Authorization: `Bearer ${token}` },
	});
	return { ok: response.ok };
}

/**
 * Reads the port the backend announces on stdout.
 *
 * A backend that exits instead of announcing must reject rather than hang: the supervisor
 * distinguishes a credential-unlock exit from a failure by that rejection, so a probe that
 * waited forever would turn a recoverable exit into a stuck launch.
 */
function readPort(
	child: ChildProcess,
	timeoutMs: number,
	onProgress?: (progress: StartupProgress) => void,
): Promise<number> {
	const stdout = child.stdout;
	if (!stdout)
		throw new Error("Backend stdout is unavailable for the port announcement.");

	return new Promise((resolve, reject) => {
		let output = "";
		let processed = 0;

		const settle = (finish: () => void) => {
			stdout.off("data", onData);
			child.off("exit", onExit);
			child.off("error", onError);
			clearTimeout(timer);
			finish();
		};

		const onData = (chunk: Buffer | string) => {
			output += typeof chunk === "string" ? chunk : chunk.toString("utf8");
			const completeEnd = output.lastIndexOf("\n") + 1;
			if (completeEnd > processed) {
				for (const text of output
					.slice(processed, completeEnd)
					.split(/\r?\n/)) {
					const progress = parseStartupProgress(text);
					if (!progress) continue;
					timer.refresh();
					onProgress?.(progress);
				}
				processed = completeEnd;
			}
			const line = output
				.split(/\r?\n/)
				.find((candidate) => candidate.startsWith(portPrefix));
			if (!line) return;

			const port = Number.parseInt(line.slice(portPrefix.length), 10);
			settle(() =>
				Number.isInteger(port) && port > 0
					? resolve(port)
					: reject(new Error("Backend reported an invalid loopback port.")),
			);
		};

		const onExit = () =>
			settle(() =>
				reject(new Error("Backend exited before reporting its port.")),
			);
		const onError = () =>
			settle(() =>
				reject(new Error("Backend process failed before reporting its port.")),
			);
		const timer = setTimeout(
			() =>
				settle(() =>
					reject(new Error("Backend did not report a port in time.")),
				),
			timeoutMs,
		);

		stdout.on("data", onData);
		child.once("exit", onExit);
		child.once("error", onError);
	});
}

function parseStartupProgress(line: string): StartupProgress | null {
	if (!line.startsWith(startupPrefix)) return null;
	const [phase, rawFraction] = line.slice(startupPrefix.length).split(":");
	if (!phase) return null;
	const fraction = Number.parseFloat(rawFraction ?? "");
	return {
		phase,
		fraction: Number.isFinite(fraction)
			? Math.min(Math.max(fraction, 0), 1)
			: null,
	};
}
