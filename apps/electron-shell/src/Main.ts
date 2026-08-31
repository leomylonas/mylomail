import { fileURLToPath } from "node:url";
import { dirname, join } from "node:path";
import { app, BrowserWindow, ipcMain, session } from "electron";
import { startBackend } from "@mylomail/electron-shell/BackendSupervisor";
import { waitForBackendHealth } from "@mylomail/electron-shell/BackendHealthProbe";
import {
	backendConnectionChannel,
	type BackendConnection,
} from "@mylomail/electron-shell/BackendConnection";
import { promptForMasterPassword } from "@mylomail/electron-shell/MasterPassword/MasterPasswordPrompt";

export const backendMode =
	process.env.ELECTRON_BACKEND_MODE === "attach" ? "attach" : "spawn";

const here = dirname(fileURLToPath(import.meta.url));

/**
 * Launches the backend, then the window.
 *
 * The order is the contract from §9: Electron spawns the backend, waits for it to report a
 * port and answer `/health`, and only then lets a renderer exist. A window that opened first
 * would have to handle a backend that is not there yet, which is a state the rest of the app
 * would then have to model forever.
 */
export async function startShell(): Promise<void> {
	const backend = await startBackend({
		command: process.env.MYLOMAIL_BACKEND_COMMAND ?? "dotnet",
		args: (process.env.MYLOMAIL_BACKEND_ARGS ?? "").split(" ").filter(Boolean),
		requestMasterPassword: promptForMasterPassword,
		waitUntilReady: (launch) => waitForBackendHealth(launch),
	});

	const origin = `http://127.0.0.1:${backend.port}`;
	const connection: BackendConnection = { origin };

	// Set before any window exists, so the very first document request is authenticated.
	// httpOnly keeps it out of reach of page script: the renderer authenticates without ever
	// holding the token.
	await session.defaultSession.cookies.set({
		url: origin,
		name: "mylomail_launch",
		value: backend.launchToken,
		httpOnly: true,
		sameSite: "strict",
	});

	// Held here and handed over on request, so the token never reaches a command line.
	ipcMain.handle(backendConnectionChannel, () => connection);

	// The port, never the token: this line is diagnostics, and the token is the backend's
	// only defence against another local process.
	console.info(`Backend ready on 127.0.0.1:${backend.port}.`);

	// The backend is a child of this process, so it must not outlive it.
	app.on("before-quit", () => backend.child.kill("SIGTERM"));

	await createWindow(origin);
}

async function createWindow(origin: string): Promise<BrowserWindow> {
	const window = new BrowserWindow({
		width: 1280,
		height: 800,
		show: false,
		webPreferences: {
			preload: join(here, "Preload.cjs"),

			// The renderer displays remote-authored message HTML, so it never gets Node.
			contextIsolation: true,
			nodeIntegration: false,
			sandbox: true,
		},
	});

	window.once("ready-to-show", () => window.show());
	window.webContents.on("did-fail-load", (_e, code, description, url) =>
		console.error(`load failed ${code} ${description} ${url}`),
	);
	window.webContents.on("render-process-gone", (_e, details) =>
		console.error(`renderer gone: ${details.reason}`),
	);

	// Without this a renderer failure is invisible: the window simply shows nothing, and the
	// main process's log stays clean while the app is broken.
	window.webContents.on("console-message", (event) => {
		console.info(`[renderer] ${event.message}`);
	});
	// Loaded over http from the backend rather than from disk: same-origin is what lets one
	// httpOnly cookie authenticate documents, assets, fetches and the WebSocket handshake
	// alike, and keeps the launch token out of every URL.
	await window.loadURL(origin);
	return window;
}

/**
 * The entry point.
 *
 * A failure here is fatal and says so: with no backend there is nothing for a window to show,
 * and §9 requires the failure be surfaced rather than retried into a silent loop.
 */
app
	.whenReady()
	.then(startShell)
	.catch((error: unknown) => {
		console.error("MyloMail could not start:", error);
		app.exit(1);
	});

app.on("window-all-closed", () => app.quit());
