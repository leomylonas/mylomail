import { fileURLToPath } from "node:url";
import { basename, dirname, join } from "node:path";
import {
	app,
	BrowserWindow,
	dialog,
	ipcMain,
	Notification,
	session,
	shell,
} from "electron";
import { startBackend } from "@mylomail/electron-shell/BackendSupervisor";
import { waitForBackendHealth } from "@mylomail/electron-shell/BackendHealthProbe";
import {
	backendConnectionChannel,
	notificationClickedChannel,
	openAttachmentChannel,
	openWindowChannel,
	showNotificationChannel,
	type BackendConnection,
	type NotificationClicked,
	type NotificationRequest,
	type OpenWindowRequest,
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

	// The backend's own output, which was piped and then never read — so anything it logged,
	// including every unhandled error, went into a pipe nobody drained. Forwarded rather than
	// inherited so it stays distinguishable from the shell's own logging.
	backend.child.stderr?.on("data", (chunk: Buffer) =>
		process.stderr.write(`[backend] ${chunk.toString("utf8")}`),
	);

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
	ipcMain.handle(
		openAttachmentChannel,
		async (event, messageId: unknown, attachmentId: unknown) => {
			if (!isGuid(messageId) || !isGuid(attachmentId)) {
				throw new Error("A valid attachment is required.");
			}

			// Electron, not the renderer, asks the authenticated backend to materialise the
			// copy. A renderer can therefore never hand shell.openPath an arbitrary local path.
			const response = await session.defaultSession.fetch(
				`${origin}/messages/${messageId}/attachments/${attachmentId}/open`,
				{ method: "POST" },
			);
			if (!response.ok) throw new Error("Could not prepare this attachment.");
			const { path } = (await response.json()) as { path?: unknown };
			if (typeof path !== "string" || !isAttachmentTempPath(path)) {
				throw new Error("The backend returned an invalid attachment path.");
			}

			if (isDangerousAttachment(path)) {
				const answer = await dialog.showMessageBox(
					BrowserWindow.fromWebContents(event.sender)!,
					{
						type: "warning",
						buttons: ["Open", "Cancel"],
						defaultId: 1,
						cancelId: 1,
						message: "This attachment may run code.",
						detail: `Open ${basename(path)} anyway?`,
					},
				);
				if (answer.response !== 0) return "";
			}

			return shell.openPath(path);
		},
	);

	// Dispatch is the shell's job, not the renderer's (§13 Epic 9): only main process code
	// calls the native Notification API. A click focuses every open window and hands it the
	// message id, which is as far as this goes until Epic 10 gives windows independent
	// identities to navigate against.
	ipcMain.handle(showNotificationChannel, (_event, request: unknown): void => {
		if (!isNotificationRequest(request)) {
			throw new Error("A valid notification request is required.");
		}

		const notification = new Notification({
			title: request.title,
			body: request.body,
		});
		notification.on("click", () => {
			const clicked: NotificationClicked = {
				notificationId: request.id,
				messageId: request.messageId,
			};
			for (const window of BrowserWindow.getAllWindows()) {
				if (window.isMinimized()) window.restore();
				window.show();
				window.focus();
				// Always sent, even with messageId null (the message hasn't replayed
				// locally yet, §3): the renderer is what resolves that case on demand,
				// not the shell, which keeps no notification state of its own.
				window.webContents.send(notificationClickedChannel, clicked);
			}
		});
		notification.show();
	});

	// The port, never the token: this line is diagnostics, and the token is the backend's
	// only defence against another local process.
	console.info(`Backend ready on 127.0.0.1:${backend.port}.`);

	// One additional main window, one message window, one popped-out compose window — all the
	// same shell, all sharing this one backend connection regardless of window count (§13
	// Epic 10). The renderer asks for a shape; only the shell decides bounds and offset.
	ipcMain.handle(
		openWindowChannel,
		async (event, request: unknown): Promise<void> => {
			if (!isOpenWindowRequest(request)) {
				throw new Error("A valid window request is required.");
			}

			const opener = BrowserWindow.fromWebContents(event.sender);
			const window = await createWindow(origin, {
				query: request.query,
				bounds: opener ? offsetBounds(opener.getBounds()) : undefined,
			});
			trackBoundsPersistence(window, origin);
		},
	);

	// The bounds convention (§13): a newly opened window inherits the primary/last-active
	// window's last-known size and position, offset slightly — not a fully independent bounds
	// history per window identity. Persisted globally, read once at startup for the first
	// window; every later window in this run instead offsets from whichever window opened it.
	const savedBounds = await loadWindowBounds(origin);
	const first = await createWindow(origin, { bounds: savedBounds });
	trackBoundsPersistence(first, origin);

	// The backend is a child of this process, so it must not outlive it.
	app.on("before-quit", () => backend.child.kill("SIGTERM"));
}

interface WindowBounds {
	width: number;
	height: number;
	x?: number;
	y?: number;
}

function offsetBounds(bounds: WindowBounds): WindowBounds {
	return {
		width: bounds.width,
		height: bounds.height,
		x: bounds.x === undefined ? undefined : bounds.x + 24,
		y: bounds.y === undefined ? undefined : bounds.y + 24,
	};
}

async function loadWindowBounds(
	origin: string,
): Promise<WindowBounds | undefined> {
	try {
		const response = await session.defaultSession.fetch(
			`${origin}/shell-settings`,
		);
		if (!response.ok) return undefined;
		const settings = (await response.json()) as {
			windowBoundsJson?: string | null;
		};
		if (!settings.windowBoundsJson) return undefined;
		const bounds = JSON.parse(settings.windowBoundsJson) as unknown;
		return isWindowBounds(bounds) ? bounds : undefined;
	} catch {
		// No persisted bounds yet, or the backend isn't answering this early — the default
		// size below is a perfectly good first launch.
		return undefined;
	}
}

/** Debounced so dragging a window doesn't fire a PUT per pixel. */
function trackBoundsPersistence(window: BrowserWindow, origin: string): void {
	let timer: ReturnType<typeof setTimeout> | undefined;
	const persist = () => {
		clearTimeout(timer);
		timer = setTimeout(() => {
			const bounds = window.getBounds();
			void session.defaultSession
				.fetch(`${origin}/shell-settings/window-bounds`, {
					method: "PUT",
					headers: { "Content-Type": "application/json" },
					body: JSON.stringify({ windowBoundsJson: JSON.stringify(bounds) }),
				})
				.catch(() => {
					// Best-effort: losing the next window's starting position is not worth
					// surfacing to the user.
				});
		}, 500);
	};
	window.on("resize", persist);
	window.on("move", persist);
}

async function createWindow(
	origin: string,
	options?: { query?: string; bounds?: WindowBounds },
): Promise<BrowserWindow> {
	const bounds = options?.bounds;
	const window = new BrowserWindow({
		width: bounds?.width ?? 1280,
		height: bounds?.height ?? 800,
		x: bounds?.x,
		y: bounds?.y,
		show: false,
		webPreferences: {
			preload: join(here, "Preload.cjs"),

			// The renderer displays remote-authored message HTML, so it never gets Node.
			contextIsolation: true,
			nodeIntegration: false,
			sandbox: true,
		},
	});

	// Navigation and window-open interception (§13). Message content is remote-authored, and
	// the renderer holds the capability to mutate mail — so a link that navigated the window
	// would replace a privileged document with an attacker's page. Nothing navigates: links
	// open in the user's browser, where they belong, and anything else is refused.
	window.webContents.setWindowOpenHandler(({ url }) => {
		void openExternally(url);
		return { action: "deny" };
	});

	window.webContents.on("will-navigate", (event, url) => {
		if (url !== origin && !url.startsWith(`${origin}/`)) {
			event.preventDefault();
			void openExternally(url);
		}
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
	// alike, and keeps the launch token out of every URL. The query string only ever picks a
	// view within that same document — never a different origin.
	const url = options?.query ? `${origin}/?${options.query}` : origin;
	await window.loadURL(url);
	return window;
}

function isWindowBounds(value: unknown): value is WindowBounds {
	if (typeof value !== "object" || value === null) return false;
	const candidate = value as Record<string, unknown>;
	return (
		typeof candidate.width === "number" && typeof candidate.height === "number"
	);
}

function isOpenWindowRequest(value: unknown): value is OpenWindowRequest {
	if (typeof value !== "object" || value === null) return false;
	const candidate = value as Record<string, unknown>;
	return candidate.query === undefined || typeof candidate.query === "string";
}

/**
 * Hands a link to the operating system, if it is one worth handing over.
 *
 * Only http and https. A message can contain any scheme it likes, and passing `file:`, or a
 * custom scheme registered by some other application, to the OS is how a link in an email
 * becomes code execution.
 */
async function openExternally(url: string): Promise<void> {
	try {
		const parsed = new URL(url);
		if (parsed.protocol === "http:" || parsed.protocol === "https:") {
			await shell.openExternal(url);
		}
	} catch {
		// Not a URL at all. Nothing to open, and nothing to report: this is a link in
		// someone else's email, not a fault in the app.
	}
}

const guid = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const dangerousExtensions = new Set([
	".bat",
	".cmd",
	".desktop",
	".exe",
	".msi",
	".ps1",
	".sh",
]);

function isGuid(value: unknown): value is string {
	return typeof value === "string" && guid.test(value);
}

function isNotificationRequest(value: unknown): value is NotificationRequest {
	if (typeof value !== "object" || value === null) return false;
	const candidate = value as Record<string, unknown>;
	return (
		isGuid(candidate.id) &&
		(candidate.messageId === null || isGuid(candidate.messageId)) &&
		typeof candidate.title === "string" &&
		typeof candidate.body === "string"
	);
}

function isAttachmentTempPath(path: string): boolean {
	const parent = dirname(path);
	return (
		basename(dirname(parent)) === "attachments" && guid.test(basename(parent))
	);
}

function isDangerousAttachment(path: string): boolean {
	const extension = basename(path)
		.slice(basename(path).lastIndexOf("."))
		.toLowerCase();
	return dangerousExtensions.has(extension);
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
