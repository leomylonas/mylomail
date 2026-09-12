import { randomBytes } from "node:crypto";
import { spawn, spawnSync, type ChildProcess } from "node:child_process";
import { chmodSync, mkdtempSync, writeFileSync, mkdirSync } from "node:fs";
import { tmpdir } from "node:os";
import { dirname, join } from "node:path";
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
	restartElectron(): Promise<Pick<LaunchedApp, "app" | "window">>;
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
	existingDataDirectory?: string,
): Promise<LaunchedApp> {
	const fixture = createFixtureEnvironment(environment, existingDataDirectory);
	const { app, window } = await launchElectron(activationArguments, {
		...fixture.environment,
		MYLOMAIL_BACKEND_COMMAND:
			fixture.environment.MYLOMAIL_BACKEND_COMMAND ?? "dotnet",
		// The already-built assembly, not `dotnet run --project`: MSBuild in the launch
		// path made startup nondeterministic — every spec launches its own app, and a
		// project-lock or restore check that stalls shows up as the window never
		// appearing, which reads as an app hang rather than as a build one.
		MYLOMAIL_BACKEND_ARGS:
			fixture.environment.MYLOMAIL_BACKEND_ARGS ?? backendAssembly,
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
		const electronEnvironment = {
			...fixture.environment,
			ELECTRON_BACKEND_MODE: "attach",
			BACKEND_URL: `http://127.0.0.1:${port}`,
			MYLOMAIL_LAUNCH_TOKEN: launchToken,
			// A successful test therefore proves Main never attempted the spawn path.
			MYLOMAIL_BACKEND_COMMAND: "mylomail-attach-must-not-spawn",
		};
		const { app, window } = await launchElectron([], electronEnvironment);
		return {
			app,
			window,
			dataDirectory: fixture.dataDirectory,
			backend,
			stopBackend,
			restartElectron: () => launchElectron([], electronEnvironment),
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

/** Launches electron-builder's unpacked application, including its self-contained backend. */
export async function launchPackagedApp(
	environment: Readonly<NodeJS.ProcessEnv> = {},
): Promise<LaunchedApp> {
	const fixture = createFixtureEnvironment(environment);
	const { app, window } = await launchElectron(
		[],
		fixture.environment,
		packagedExecutablePath(),
	);
	return {
		app,
		window,
		dataDirectory: fixture.dataDirectory,
	};
}

function createFixtureEnvironment(
	overrides: Readonly<NodeJS.ProcessEnv>,
	existingDataDirectory?: string,
): {
	dataDirectory: string;
	environment: Record<string, string>;
} {
	const root = existingDataDirectory
		? dirname(existingDataDirectory)
		: mkdtempSync(join(tmpdir(), "mylomail-e2e-"));
	const dataDirectory = existingDataDirectory ?? join(root, "data");
	const configHome = join(root, "config");
	mkdirSync(join(configHome, "mylomail"), { recursive: true });
	mkdirSync(dataDirectory, { recursive: true });
	writeFileSync(
		join(configHome, "mylomail", "bootstrap.json"),
		JSON.stringify({ DataDirectoryOverride: dataDirectory }),
	);
	const attachmentOpenMarker = overrides.MYLOMAIL_E2E_ATTACHMENT_OPEN_MARKER;
	if (attachmentOpenMarker) {
		const applications = join(root, "share", "applications");
		const opener = join(root, "attachment-opener");
		mkdirSync(applications, { recursive: true });
		writeFileSync(
			opener,
			`#!/usr/bin/env node\nrequire("node:fs").writeFileSync(${JSON.stringify(attachmentOpenMarker)}, process.argv[2]);\n`,
		);
		chmodSync(opener, 0o700);
		writeFileSync(
			join(applications, "mylomail-e2e-opener.desktop"),
			[
				"[Desktop Entry]",
				"Type=Application",
				"Name=MyloMail attachment verifier",
				`Exec=${opener} %f`,
				"MimeType=text/plain;",
				"NoDisplay=true",
				"",
			].join("\n"),
		);
		writeFileSync(
			join(configHome, "mimeapps.list"),
			[
				"[Default Applications]",
				"text/plain=mylomail-e2e-opener.desktop;",
				"",
			].join("\n"),
		);
		const desktopDatabase = spawnSync(
			"update-desktop-database",
			[applications],
			{
				env: {
					...process.env,
					XDG_CONFIG_HOME: configHome,
					XDG_DATA_HOME: join(root, "share"),
				},
				encoding: "utf8",
			},
		);
		if (desktopDatabase.status !== 0) {
			throw new Error(
				`Could not register attachment verifier: ${desktopDatabase.stderr}`,
			);
		}
		const mimeEnvironment = {
			...process.env,
			XDG_CONFIG_HOME: configHome,
			XDG_DATA_HOME: join(root, "share"),
		};
		const association = spawnSync(
			"xdg-mime",
			["default", "mylomail-e2e-opener.desktop", "text/plain"],
			{ env: mimeEnvironment, encoding: "utf8" },
		);
		const selected = spawnSync("xdg-mime", ["query", "default", "text/plain"], {
			env: mimeEnvironment,
			encoding: "utf8",
		});
		if (
			association.status !== 0 ||
			selected.status !== 0 ||
			selected.stdout.trim() !== "mylomail-e2e-opener.desktop"
		) {
			throw new Error(
				`Could not select attachment verifier: ${association.stderr}${selected.stderr}`,
			);
		}
	}
	const inherited: NodeJS.ProcessEnv = {
		...process.env,
		...overrides,
		XDG_CONFIG_HOME: configHome,
		XDG_DATA_HOME: join(root, "share"),
		MYLOMAIL_MASTER_PASSWORD: "e2e-master-password",
		MYLOMAIL_RENDERER_PATH: join(repositoryRoot, "apps/renderer/dist"),
	};
	if (overrides.DBUS_SESSION_BUS_ADDRESS === "") {
		delete inherited.DBUS_SESSION_BUS_ADDRESS;
	}
	const environment: Record<string, string> = {};
	for (const [name, value] of Object.entries(inherited)) {
		if (value !== undefined) environment[name] = value;
	}
	return { dataDirectory, environment };
}

async function launchElectron(
	activationArguments: readonly string[],
	environment: Record<string, string>,
	executablePath?: string,
): Promise<{ app: ElectronApplication; window: Page }> {
	const app = await _electron.launch({
		args: [
			...(executablePath
				? []
				: [join(repositoryRoot, "apps/electron-shell/dist/Main.js")]),
			"--headless",
			"--disable-gpu",
			"--no-sandbox",
			...activationArguments,
		],
		...(executablePath ? { executablePath } : {}),
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

function packagedExecutablePath(): string {
	if (process.env.MYLOMAIL_PACKAGED_EXECUTABLE) {
		return process.env.MYLOMAIL_PACKAGED_EXECUTABLE;
	}
	if (process.platform === "win32") {
		return join(repositoryRoot, "dist/packages/win-unpacked/MyloMail.exe");
	}
	if (process.platform === "darwin") {
		const directory = process.arch === "arm64" ? "mac-arm64" : "mac";
		return join(
			repositoryRoot,
			`dist/packages/${directory}/MyloMail.app/Contents/MacOS/MyloMail`,
		);
	}
	return join(repositoryRoot, "dist/packages/linux-unpacked/mylomail");
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
