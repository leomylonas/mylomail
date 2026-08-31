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
			MYLOMAIL_BACKEND_ARGS: [
				"run",
				"--project",
				join(repositoryRoot, "server/MyloMail.Api/MyloMail.Api.csproj"),
				"--no-launch-profile",
				"--no-build",
			].join(" "),
			MYLOMAIL_RENDERER_PATH: join(repositoryRoot, "apps/renderer/dist"),
		},
	});

	const window = await app.firstWindow();
	return { app, window, dataDirectory };
}
