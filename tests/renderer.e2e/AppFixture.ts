import { randomBytes } from "node:crypto";
import { spawn, type ChildProcess } from "node:child_process";
import { mkdtempSync, writeFileSync, mkdirSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { fileURLToPath } from "node:url";
import {
	_electron,
	type ElectronApplication,
	type Page,
} from "@playwright/test";
import { waitForBackendHealth } from "@mylomail/electron-shell/BackendHealthProbe";

const repositoryRoot = fileURLToPath(new URL("../..", import.meta.url));

export interface LaunchedApp {
	app: ElectronApplication;
	window: Page;
	dataDirectory: string;
}

export interface AttachedLaunchedApp extends LaunchedApp {
	backend: ChildProcess;
	stopBackend(): Promise<void>;
}

/**
 * Launches the packaged shell exactly as a user would, against a throwaway data directory.
 *
 * A master password is supplied through the environment because this machine has no OS
 * credential store and nothing can click the prompt — the same path CI takes (§9).
 */
export async function launchApp(
	activationArguments: readonly string[] = [],
	environment: Readonly<NodeJS.ProcessEnv> = {},
): Promise<LaunchedApp> {
	const fixture = createFixtureEnvironment(environment);
	const { app, window } = await launchElectron(activationArguments, {
		...fixture.environment,
		MYLOMAIL_BACKEND_COMMAND: "dotnet",
		// The already-built assembly, not `dotnet run --project`: MSBuild in the launch
		// path made startup nondeterministic — every spec launches its own app, and a
		// project-lock or restore check that stalls shows up as the window never
		// appearing, which reads as an app hang rather than as a build one.
		MYLOMAIL_BACKEND_ARGS: backendAssembly,
	});
	return {
		app,
		window,
		dataDirectory: fixture.dataDirectory,
	};
}

/**
 * Starts the API independently, then launches Electron in attach mode. The returned stop
 * function is intentionally separate from `app.close()`: the attach-mode contract is that
 * Electron never owns the external process lifetime.
 */
export async function launchAttachedApp(
	environment: Readonly<NodeJS.ProcessEnv> = {},
): Promise<AttachedLaunchedApp> {
	const fixture = createFixtureEnvironment(environment);
	const launchToken = randomBytes(32).toString("base64url");
	const backend = spawn("dotnet", [backendAssembly], {
		env: {
			...fixture.environment,
			MYLOMAIL_LAUNCH_TOKEN: launchToken,
		},
		stdio: ["ignore", "pipe", "pipe"],
	});
	backend.stderr?.on("data", (chunk: Buffer) => {
		process.stderr.write(`[attached backend] ${chunk.toString("utf8")}`);
	});
	const stopBackend = () => stopChild(backend);

	try {
		const port = await waitForBackendHealth({ child: backend, launchToken });
		const { app, window } = await launchElectron([], {
			...fixture.environment,
			ELECTRON_BACKEND_MODE: "attach",
			BACKEND_URL: `http://127.0.0.1:${port}`,
			MYLOMAIL_LAUNCH_TOKEN: launchToken,
			// A successful test therefore proves Main never attempted the spawn path.
			MYLOMAIL_BACKEND_COMMAND: "mylomail-attach-must-not-spawn",
		});
		return {
			app,
			window,
			dataDirectory: fixture.dataDirectory,
			backend,
			stopBackend,
		};
	} catch (error) {
		await stopBackend();
		throw error;
	}
}

const backendAssembly = join(
	repositoryRoot,
	"server/MyloMail.Api/bin/Debug/net10.0/MyloMail.Api.dll",
);

function createFixtureEnvironment(overrides: Readonly<NodeJS.ProcessEnv>): {
	dataDirectory: string;
	environment: Record<string, string>;
} {
	const root = mkdtempSync(join(tmpdir(), "mylomail-e2e-"));
	const dataDirectory = join(root, "data");
	const configHome = join(root, "config");
	mkdirSync(join(configHome, "mylomail"), { recursive: true });
	mkdirSync(dataDirectory, { recursive: true });
	writeFileSync(
		join(configHome, "mylomail", "bootstrap.json"),
		JSON.stringify({ DataDirectoryOverride: dataDirectory }),
	);
	const inherited = {
		...process.env,
		...overrides,
		XDG_CONFIG_HOME: configHome,
		XDG_DATA_HOME: join(root, "share"),
		MYLOMAIL_MASTER_PASSWORD: "e2e-master-password",
		MYLOMAIL_RENDERER_PATH: join(repositoryRoot, "apps/renderer/dist"),
	};
	const environment: Record<string, string> = {};
	for (const [name, value] of Object.entries(inherited)) {
		if (value !== undefined) environment[name] = value;
	}
	return { dataDirectory, environment };
}

async function launchElectron(
	activationArguments: readonly string[],
	environment: Record<string, string>,
): Promise<{ app: ElectronApplication; window: Page }> {
	const app = await _electron.launch({
		args: [
			join(repositoryRoot, "apps/electron-shell/dist/Main.js"),
			"--headless",
			"--disable-gpu",
			"--no-sandbox",
			...activationArguments,
		],
		env: environment,
	});
	const window = await app.firstWindow();
	window.on("pageerror", (error) => {
		console.error(`[renderer] ${error.stack ?? error.message}`);
	});
	window.on("console", (message) => {
		if (message.type() === "error")
			console.error(`[renderer] ${message.text()}`);
	});
	return { app, window };
}

async function stopChild(child: ChildProcess): Promise<void> {
	if (child.exitCode !== null) return;
	await new Promise<void>((resolve) => {
		const timeout = setTimeout(() => child.kill("SIGKILL"), 3000);
		child.once("exit", () => {
			clearTimeout(timeout);
			resolve();
		});
		child.kill("SIGTERM");
	});
}
