import { randomBytes } from "node:crypto";
import { spawn, type ChildProcess } from "node:child_process";

/** Exit code emitted by the backend when it needs a master-password restart. */
export const credentialStoreUnavailableExitCode = 78;

/** The last of a backend's stderr, kept so a failed launch can say why rather than only that. */
const stderrTails = new WeakMap<ChildProcess, string>();
const stderrTailLimit = 4000;

/** How a launch ended, as the supervisor decides whether to prompt, retry or give up. */
interface LaunchFailure {
	exitCode: number | null;
	/** Why, when it was not a plain exit — e.g. the port was never announced in time. */
	reason?: string;
}

export interface BackendLaunch {
	child: ChildProcess;
	launchToken: string;
}

/** A launch that has answered `/health`, and the loopback port it answered on. */
export interface ReadyBackend extends BackendLaunch {
	port: number;
}

export interface BackendSupervisorOptions {
	command: string;
	args: readonly string[];
	requestMasterPassword(): Promise<string | undefined>;

	/**
	 * Resolves with the loopback port once the backend answers. The port is assigned by the
	 * OS, so this is the only route by which anything else learns how to reach it.
	 */
	waitUntilReady(backend: BackendLaunch): Promise<number>;
	spawnProcess?(
		command: string,
		args: readonly string[],
		options: { env: NodeJS.ProcessEnv; stdio: ["pipe", "pipe", "pipe"] },
	): ChildProcess;
}

/**
 * Starts the backend without credentials first. A deliberate code-78 exit is the only
 * condition that triggers the shell's setup/unlock UI and a one-time restarted process.
 */
export async function startBackend(
	options: BackendSupervisorOptions,
): Promise<ReadyBackend> {
	const spawnProcess = options.spawnProcess ?? spawn;
	const initial = launch(options, spawnProcess);
	try {
		return {
			...initial,
			port: await waitForReadyOrExit(initial, options.waitUntilReady),
		};
	} catch (failure) {
		stopBackend(initial);
		const { exitCode, reason } = failure as LaunchFailure;
		if (exitCode !== credentialStoreUnavailableExitCode)
			throw new Error(describeFailure(initial, exitCode, reason));
	}

	const password = await options.requestMasterPassword();
	if (!password)
		throw new Error("Credential storage requires a master password.");
	const restarted = launch(options, spawnProcess, password);
	try {
		return {
			...restarted,
			port: await waitForReadyOrExit(restarted, options.waitUntilReady),
		};
	} catch (failure) {
		stopBackend(restarted);
		const { exitCode, reason } = failure as LaunchFailure;
		throw new Error(describeFailure(restarted, exitCode, reason));
	}
}

function launch(
	options: BackendSupervisorOptions,
	spawnProcess: NonNullable<BackendSupervisorOptions["spawnProcess"]>,
	masterPassword?: string,
): BackendLaunch {
	const inheritedEnvironment = { ...process.env };
	delete inheritedEnvironment.MYLOMAIL_LAUNCH_TOKEN;
	delete inheritedEnvironment.MYLOMAIL_MASTER_PASSWORD;
	const launchToken = randomBytes(32).toString("base64url");
	const child = spawnProcess(options.command, options.args, {
		env: inheritedEnvironment,
		stdio: ["pipe", "pipe", "pipe"],
	});
	// stdin is a private inherited capability, unlike an inspectable environment or command
	// line. The backend consumes this one JSON object before it starts listening.
	child.stdin?.end(JSON.stringify({ launchToken, masterPassword }));
	// Read from the first byte rather than once startup succeeds: an unhandled exception during
	// startup is written here, and was previously lost in a pipe nobody drained. Forwarded, not
	// inherited, so it stays distinguishable from the shell's own logging.
	child.stderr?.on("data", (chunk: Buffer) => {
		const text = chunk.toString("utf8");
		process.stderr.write(`[backend] ${text}`);
		stderrTails.set(
			child,
			((stderrTails.get(child) ?? "") + text).slice(-stderrTailLimit),
		);
	});
	return { child, launchToken };
}

function describeFailure(
	backend: BackendLaunch,
	exitCode: number | null,
	reason: string | undefined,
): string {
	const signal = backend.child.signalCode;
	const what =
		exitCode !== null
			? `Backend exited during startup with code ${exitCode}.`
			: `Backend failed during startup${signal ? ` (${signal})` : ""}.`;
	const tail = stderrTails.get(backend.child)?.trim();
	return [what, reason, tail ? `Backend output:\n${tail}` : undefined]
		.filter(Boolean)
		.join(" ");
}

function stopBackend(backend: BackendLaunch): void {
	if (!backend.child.killed) backend.child.kill("SIGTERM");
}

function waitForReadyOrExit(
	backend: BackendLaunch,
	waitUntilReady: BackendSupervisorOptions["waitUntilReady"],
): Promise<number> {
	return new Promise((resolve, reject) => {
		const onExit = (code: number | null) =>
			reject({ exitCode: code } satisfies LaunchFailure);
		const onError = (error: Error) =>
			reject({
				exitCode: null,
				reason: `Could not run the backend: ${error.message}`,
			} satisfies LaunchFailure);
		backend.child.once("exit", onExit);
		backend.child.once("error", onError);
		void waitUntilReady(backend).then(
			(port) => {
				backend.child.off("exit", onExit);
				backend.child.off("error", onError);
				resolve(port);
			},
			(error: unknown) => {
				backend.child.off("exit", onExit);
				backend.child.off("error", onError);
				reject({
					exitCode: null,
					reason: error instanceof Error ? error.message : undefined,
				} satisfies LaunchFailure);
			},
		);
	});
}
