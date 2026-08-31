import { randomBytes } from "node:crypto";
import { spawn, type ChildProcess } from "node:child_process";

/** Exit code emitted by the backend when it needs a master-password restart. */
export const credentialStoreUnavailableExitCode = 78;

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
		options: { env: NodeJS.ProcessEnv; stdio: ["ignore", "pipe", "pipe"] },
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
	} catch (exitCode) {
		if (exitCode !== credentialStoreUnavailableExitCode)
			throw new Error(
				`Backend exited during startup with code ${exitCode ?? "unknown"}.`,
			);
	}

	const password = await options.requestMasterPassword();
	if (!password)
		throw new Error("Credential storage requires a master password.");
	const restarted = launch(options, spawnProcess, password);
	return {
		...restarted,
		port: await waitForReadyOrExit(restarted, options.waitUntilReady),
	};
}

function launch(
	options: BackendSupervisorOptions,
	spawnProcess: NonNullable<BackendSupervisorOptions["spawnProcess"]>,
	masterPassword?: string,
): BackendLaunch {
	const inheritedEnvironment = { ...process.env };
	delete inheritedEnvironment.MYLOMAIL_MASTER_PASSWORD;
	const launchToken = randomBytes(32).toString("base64url");
	const child = spawnProcess(options.command, options.args, {
		env: {
			...inheritedEnvironment,
			MYLOMAIL_LAUNCH_TOKEN: launchToken,
			...(masterPassword === undefined
				? {}
				: { MYLOMAIL_MASTER_PASSWORD: masterPassword }),
		},
		stdio: ["ignore", "pipe", "pipe"],
	});
	return { child, launchToken };
}

function waitForReadyOrExit(
	backend: BackendLaunch,
	waitUntilReady: BackendSupervisorOptions["waitUntilReady"],
): Promise<number> {
	return new Promise((resolve, reject) => {
		const onExit = (code: number | null) => reject(code);
		const onError = () => reject(null);
		backend.child.once("exit", onExit);
		backend.child.once("error", onError);
		void waitUntilReady(backend).then(
			(port) => {
				backend.child.off("exit", onExit);
				backend.child.off("error", onError);
				resolve(port);
			},
			() => {
				backend.child.off("exit", onExit);
				backend.child.off("error", onError);
				reject(null);
			},
		);
	});
}
