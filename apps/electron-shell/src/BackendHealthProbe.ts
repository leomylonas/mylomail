import type { ChildProcess } from "node:child_process";
import type { BackendLaunch } from "@mylomail/electron-shell/BackendSupervisor";

const portPrefix = "MYLOMAIL_PORT=";

export interface BackendHealthProbeOptions {
	/** Overall budget for the port announcement and the first healthy response. */
	timeoutMs?: number;
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
	const pollIntervalMs = options.pollIntervalMs ?? 100;
	const fetchHealth = options.fetchHealth ?? defaultFetchHealth;
	const delay =
		options.delay ??
		((ms: number) => new Promise<void>((resolve) => setTimeout(resolve, ms)));

	const deadline = Date.now() + timeoutMs;
	const port = await readPort(backend.child, deadline);

	// Polled, not asked once. The port is announced as soon as Kestrel binds, which is not
	// the same moment the pipeline is ready to answer.
	let lastFailure: unknown;
	while (Date.now() < deadline) {
		try {
			const response = await fetchHealth(
				`http://127.0.0.1:${port}/health`,
				backend.launchToken,
			);
			if (response.ok) return port;
			lastFailure = new Error(`health responded ${JSON.stringify(response)}`);
		} catch (error) {
			lastFailure = error;
		}
		await delay(pollIntervalMs);
	}

	throw new Error(
		`Backend did not become healthy within ${timeoutMs}ms${
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
function readPort(child: ChildProcess, deadline: number): Promise<number> {
	const stdout = child.stdout;
	if (!stdout)
		throw new Error("Backend stdout is unavailable for the port announcement.");

	return new Promise((resolve, reject) => {
		let output = "";

		const settle = (finish: () => void) => {
			stdout.off("data", onData);
			child.off("exit", onExit);
			child.off("error", onError);
			clearTimeout(timer);
			finish();
		};

		const onData = (chunk: Buffer | string) => {
			output += typeof chunk === "string" ? chunk : chunk.toString("utf8");
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
			Math.max(deadline - Date.now(), 0),
		);

		stdout.on("data", onData);
		child.once("exit", onExit);
		child.once("error", onError);
	});
}
