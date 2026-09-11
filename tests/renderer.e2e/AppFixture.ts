import { mkdtempSync, writeFileSync, mkdirSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { fileURLToPath } from "node:url";
import {
	_electron,
	type ElectronApplication,
	type Page,
} from "@playwright/test";

const repositoryRoot = fileURLToPath(new URL("../..", import.meta.url));

export interface LaunchedApp {
	app: ElectronApplication;
	window: Page;
	dataDirectory: string;
}

/**
 * Launches the packaged shell exactly as a user would, against a throwaway data directory.
 *
 * A master password is supplied through the environment because this machine has no OS
 * credential store and nothing can click the prompt — the same path CI takes (§9).
 */
export async function launchApp(): Promise<LaunchedApp> {
	const root = mkdtempSync(join(tmpdir(), "mylomail-e2e-"));
	const dataDirectory = join(root, "data");
	const configHome = join(root, "config");
	mkdirSync(join(configHome, "mylomail"), { recursive: true });
	mkdirSync(dataDirectory, { recursive: true });
	writeFileSync(
		join(configHome, "mylomail", "bootstrap.json"),
		JSON.stringify({ DataDirectoryOverride: dataDirectory }),
	);

	const app = await _electron.launch({
		args: [
			join(repositoryRoot, "apps/electron-shell/dist/Main.js"),
			"--headless",
			"--disable-gpu",
			"--no-sandbox",
		],
		env: {
			...process.env,
			XDG_CONFIG_HOME: configHome,
			XDG_DATA_HOME: join(root, "share"),
			MYLOMAIL_MASTER_PASSWORD: "e2e-master-password",
			MYLOMAIL_BACKEND_COMMAND: "dotnet",
			// The already-built assembly, not `dotnet run --project`: MSBuild in the launch
			// path made startup nondeterministic — every spec launches its own app, and a
			// project-lock or restore check that stalls shows up as the window never
			// appearing, which reads as an app hang rather than as a build one.
			MYLOMAIL_BACKEND_ARGS: join(
				repositoryRoot,
				"server/MyloMail.Api/bin/Debug/net10.0/MyloMail.Api.dll",
			),
			MYLOMAIL_RENDERER_PATH: join(repositoryRoot, "apps/renderer/dist"),
		},
	});
	const window = await app.firstWindow();

	// A renderer exception leaves the page mounted but inert, and every locator then simply
	// times out with nothing to say why. Printing it is the difference between "the app
	// hung" and the actual stack.
	window.on("pageerror", (error) => {
		console.error(`[renderer] ${error.stack ?? error.message}`);
	});
	window.on("console", (message) => {
		if (message.type() === "error")
			console.error(`[renderer] ${message.text()}`);
	});

	return { app, window, dataDirectory };
}
