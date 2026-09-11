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
	focusDraftWindowChannel,
	notificationClickedChannel,
	notificationNavigationReadyChannel,
	openAttachmentChannel,
	openWindowChannel,
	pickExportFolderChannel,
	reportDraftStateChannel,
	showNotificationChannel,
	updateCloseBehaviorChannel,
	type BackendConnection,
	type NotificationClicked,
	type NotificationRequest,
	type OpenWindowRequest,
} from "@mylomail/electron-shell/BackendConnection";
import { promptForMasterPassword } from "@mylomail/electron-shell/MasterPassword/MasterPasswordPrompt";
import { destroyTray, ensureTray } from "@mylomail/electron-shell/Tray";
import { closeBehaviorFromValue } from "@mylomail/electron-shell/CloseBehavior";
import { windowAlreadyEditing } from "@mylomail/electron-shell/DraftWindows";
import { isDangerousAttachment } from "@mylomail/electron-shell/DangerousAttachment";
import { NativeNotificationDispatcher } from "@mylomail/electron-shell/NativeNotificationDispatcher";
import { notificationTargetWindow } from "@mylomail/electron-shell/NotificationWindowTarget";

export const backendMode =
	process.env.ELECTRON_BACKEND_MODE === "attach" ? "attach" : "spawn";

const here = dirname(fileURLToPath(import.meta.url));

/**
 * Read once at startup, same as window bounds and the mailto-prompt flag (§13), then kept in
 * sync by `updateCloseBehaviorChannel` when ShellSettings saves a change mid-session.
 */
let closeBehavior: "QuitApp" | "MinimizeToTray" = "QuitApp";

/** Set once an actual quit is underway, so a window's `close` handler lets it through instead
 * of hiding it to the tray a second time. */
let quitting = false;

/**
 * Which draft (if any) each open window is currently editing — reported by the renderer
 * whenever a window's own compose pane changes, inline or detached. Runtime-only, cleared on
 * restart: this exists purely to stop the same draft being edited independently in two windows
 * at once, which would otherwise autosave as a silent last-write-wins race with no revision
 * check (there is no cross-window coordination or conflict detection on the save path itself).
 * Keyed by window id rather than the `BrowserWindow` object so a destroyed window's entry can
 * be found and removed without holding a reference to it.
 */
const draftWindows = new Map<number, string | null>();
const mainWindowIds = new Set<number>();
const notificationReadyWindowIds = new Set<number>();
let nextWindowSlot = 0;

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

	// §9: "Electron detects unexpected backend process exit and offers restart." `quitting`
	// is set before the deliberate SIGTERM this process itself sends on before-quit, so this
	// only fires for an exit nobody here asked for — the backend crashing, or being killed by
	// something outside the app. The whole app relaunches rather than re-plumbing a fresh
	// backend into the windows that already exist: a stale connection token, an origin whose
	// port just changed, and mid-flight SignalR state all become simply irrelevant on restart
	// rather than needing to be reconciled one at a time.
	backend.child.on("exit", (code) => {
		if (quitting) return;
		void offerBackendRestart(code);
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
	ipcMain.on(notificationNavigationReadyChannel, (event, ready: unknown) => {
		const window = BrowserWindow.fromWebContents(event.sender);
		if (
			!window ||
			!mainWindowIds.has(window.id) ||
			typeof ready !== "boolean"
		) {
			return;
		}
		if (ready) notificationReadyWindowIds.add(window.id);
		else notificationReadyWindowIds.delete(window.id);
	});
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

	// The renderer picks a destination for a bulk export, but has no filesystem access of its
	// own (§13 Export) — the native folder picker is a main-process capability, the same as
	// the attachment open-path handler above.
	ipcMain.handle(
		pickExportFolderChannel,
		async (event): Promise<string | null> => {
			const window = BrowserWindow.fromWebContents(event.sender);
			const result = await dialog.showOpenDialog(window!, {
				properties: ["openDirectory", "createDirectory"],
			});
			return result.canceled ? null : (result.filePaths[0] ?? null);
		},
	);

	const nativeNotifications = new NativeNotificationDispatcher((request) => {
		const notification = new Notification({
			title: request.title,
			body: request.body,
		});
		notification.on("click", () => {
			const clicked: NotificationClicked = {
				notificationId: request.id,
				accountId: request.accountId,
			};
			void navigateNotificationClick(origin, clicked).catch(
				(error: unknown) => {
					console.error(`notification navigation failed: ${String(error)}`);
				},
			);
		});
		notification.show();
	});

	// Every renderer receives `NotificationReady`, but native dispatch is the shell's job
	// (§13 Epic 9). The process-local dispatcher elects one relay by durable notification id,
	// so N open windows still produce one OS notification. A shell crash clears the election,
	// preserving the architecture's at-least-once delivery policy.
	ipcMain.handle(showNotificationChannel, (_event, request: unknown): void => {
		if (!isNotificationRequest(request)) {
			throw new Error("A valid notification request is required.");
		}

		// The renderer's own confirm-only-once-shown contract (HubConnection.ts) depends on
		// this call rejecting when the notification genuinely didn't appear — `new
		// Notification(...).show()` neither throws nor reports failure on a platform where
		// notifications aren't supported (a headless/CI Linux box, most commonly), so without
		// this check the promise would resolve, MarkNotificationDelivered would fire, and the
		// notification would be silently lost rather than redelivered on the next startup.
		if (!Notification.isSupported()) {
			throw new Error("Notifications are not supported on this platform.");
		}

		nativeNotifications.dispatch(request);
	});

	// Without this, a change made in ShellSettings only ever reaches the AppSettings row the
	// backend answers `loadCloseBehavior` from at the *next* startup — `closeBehavior` above
	// would keep acting on whatever was true when this window opened, silently ignoring a
	// setting the user just changed and saw succeed with no error (§8, §13 Epic 10).
	ipcMain.handle(updateCloseBehaviorChannel, (_event, value: unknown): void => {
		// The renderer already confirmed the write succeeded, so this trusts the value it
		// hands back rather than re-fetching.
		closeBehavior = closeBehaviorFromValue(value);
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
				isMain: !request.query,
			});
			trackBoundsPersistence(window, origin);
		},
	);

	// Reported by every compose pane, inline or detached, whenever the draft it's showing
	// changes (including `null` when it's showing none). This is the only cross-window
	// visibility into "which draft is open where" — see `draftWindows`'s own remarks.
	ipcMain.handle(reportDraftStateChannel, (event, draftId: unknown): void => {
		const window = BrowserWindow.fromWebContents(event.sender);
		if (window && (draftId === null || typeof draftId === "string")) {
			draftWindows.set(window.id, draftId);
		}
	});

	// Called before a window opens or detaches a draft, so it can focus an existing editor
	// instead of starting a second one that would silently race the first on autosave.
	ipcMain.handle(
		focusDraftWindowChannel,
		(event, draftId: unknown): boolean => {
			if (typeof draftId !== "string") {
				return false;
			}

			const requester = BrowserWindow.fromWebContents(event.sender);
			const id = windowAlreadyEditing(draftWindows, requester?.id, draftId);
			const window = id === undefined ? undefined : BrowserWindow.fromId(id);
			if (window && !window.isDestroyed()) {
				window.focus();
				return true;
			}

			return false;
		},
	);

	// The bounds convention (§13): a newly opened window inherits the primary/last-active
	// window's last-known size and position, offset slightly — not a fully independent bounds
	// history per window identity. Persisted globally, read once at startup for the first
	// window; every later window in this run instead offsets from whichever window opened it.
	closeBehavior = await loadCloseBehavior(origin);

	const savedBounds = await loadWindowBounds(origin);
	const first = await createWindow(origin, { bounds: savedBounds });
	trackBoundsPersistence(first, origin);

	// On startup, not on every launch's happy path: a user who already declined once should
	// not be asked again every time the app opens (§13, standing convention).
	void promptForMailtoDefaultAsync(origin, first);

	// The backend is a child of this process, so it must not outlive it. Quitting is
	// intercepted once to ask about pending scheduled/undo-send messages (§15) — the
	// OutboxItem row itself is durable from the moment it's queued, but Hangfire's job storage
	// is in-memory (§9), so the scheduled dispatch that would fire it is gone the moment this
	// process exits. Startup reconciliation re-enqueues it next launch, so nothing is lost —
	// only delayed until the app runs again — but a user quitting expecting an imminent send
	// to have gone out should be told that plainly rather than left to discover it later.
	// Confirmed once, the second before-quit (from the app.quit() call inside confirmQuit's
	// continuation) proceeds for real rather than asking again.
	let confirmedQuit = false;
	// Set while confirmQuit is in flight, so a second before-quit arriving before the first
	// resolves (a rapid double Cmd+Q, say) does not stack a second dialog on top of the first.
	let confirming = false;
	app.on("before-quit", (event) => {
		if (confirmedQuit) {
			quitting = true;
			destroyTray();
			backend.child.kill("SIGTERM");
			return;
		}

		event.preventDefault();
		if (confirming) return;
		confirming = true;
		void confirmQuit(origin).then((proceed) => {
			confirming = false;
			if (proceed) {
				confirmedQuit = true;
				app.quit();
			}
		});
	});
}

/**
 * The unexpected-backend-exit path from §9, distinct from the deliberate credential-store
 * restart `startBackend` already handles during startup: this fires only once a session is
 * already underway, so there is a user to tell rather than a launch sequence still deciding
 * what to do.
 */
async function offerBackendRestart(code: number | null): Promise<void> {
	const [window] = BrowserWindow.getAllWindows();
	const options: Electron.MessageBoxOptions = {
		type: "error",
		buttons: ["Restart", "Quit"],
		defaultId: 0,
		cancelId: 1,
		title: "MyloMail stopped unexpectedly",
		message: "MyloMail's background process stopped unexpectedly.",
		detail:
			code === null
				? "Restarting will reopen the app fresh. Nothing sent or saved is lost."
				: `It exited with code ${code}. Restarting will reopen the app fresh. Nothing sent or saved is lost.`,
	};
	const result =
		window && !window.isDestroyed()
			? await dialog.showMessageBox(window, options)
			: await dialog.showMessageBox(options);

	if (result.response === 0) {
		app.relaunch();
	}
	quitting = true;
	app.exit(0);
}

/**
 * Asks before quitting if anything is still waiting to send. No dialog at all when nothing
 * is pending — the common case must not gain a click just because the feature exists.
 */
async function confirmQuit(origin: string): Promise<boolean> {
	let pending = 0;
	try {
		// A short deadline, not just error handling: a *hung* backend (still accepting the
		// connection, never answering) would otherwise leave before-quit prevented forever
		// with no failure to catch — unquittable through any normal path.
		const controller = new AbortController();
		const timeout = setTimeout(() => controller.abort(), 3000);
		let response: Response;
		try {
			response = await session.defaultSession.fetch(
				`${origin}/outbox/pending-count`,
				{ signal: controller.signal },
			);
		} finally {
			clearTimeout(timeout);
		}
		// A non-2xx response is exactly as uninformative as the request failing outright —
		// both fall through to the same "can't tell, quitting is the least surprising
		// default" outcome below, not a silent "nothing pending".
		if (response.ok) pending = (await response.json()) as number;
		else return true;
	} catch {
		// Can't tell — quitting is the least surprising default over blocking the user from
		// ever closing the app because the backend stopped answering.
		return true;
	}

	if (pending === 0) return true;

	const [window] = BrowserWindow.getAllWindows();
	const options: Electron.MessageBoxOptions = {
		type: "warning",
		buttons: ["Quit Anyway", "Cancel"],
		defaultId: 1,
		cancelId: 1,
		message:
			pending === 1
				? "One message hasn't sent yet."
				: `${pending} messages haven't sent yet.`,
		detail:
			"It won't be lost, but it won't send until MyloMail is running again — scheduled " +
			"and undo-send messages only fire while the app is open.",
	};
	const result = window
		? await dialog.showMessageBox(window, options)
		: await dialog.showMessageBox(options);

	return result.response === 0;
}

async function loadCloseBehavior(
	origin: string,
): Promise<"QuitApp" | "MinimizeToTray"> {
	try {
		const response = await session.defaultSession.fetch(
			`${origin}/shell-settings`,
		);
		if (!response.ok) return "QuitApp";
		const settings = (await response.json()) as {
			closeBehavior?: unknown;
		};
		return closeBehaviorFromValue(settings.closeBehavior);
	} catch {
		// The backend isn't answering this early, or the row doesn't exist yet — quitting on
		// close is the least surprising default.
		return "QuitApp";
	}
}

/**
 * Offers to register this app as the OS `mailto:` handler, unless it already is one or the
 * user has asked not to be asked again.
 *
 * The "don't ask again" state is a shell-wide `AppSettings` flag rather than anything
 * per-window, for the same reason panel layout and window bounds are: it is a fact about the
 * installation, not about any one window.
 */
async function promptForMailtoDefaultAsync(
	origin: string,
	window: BrowserWindow,
): Promise<void> {
	if (app.isDefaultProtocolClient("mailto")) return;

	try {
		const response = await session.defaultSession.fetch(
			`${origin}/shell-settings`,
		);
		if (response.ok) {
			const settings = (await response.json()) as {
				mailtoPromptDismissed?: boolean;
			};
			if (settings.mailtoPromptDismissed) return;
		}
	} catch {
		// If the check itself fails, asking once more is the safer default over never asking.
	}

	const result = await dialog.showMessageBox(window, {
		type: "question",
		buttons: ["Make Default", "Not Now"],
		defaultId: 0,
		cancelId: 1,
		checkboxLabel: "Don't ask again",
		checkboxChecked: false,
		message: "Make MyloMail your default mail application?",
		detail: "This lets mailto: links in other apps open a new message here.",
	});

	if (result.response === 0) {
		app.setAsDefaultProtocolClient("mailto");
	}

	if (result.checkboxChecked) {
		await session.defaultSession
			.fetch(`${origin}/shell-settings/mailto-prompt-dismissed`, {
				method: "PUT",
				headers: { "Content-Type": "application/json" },
				body: JSON.stringify({ dismissed: true }),
			})
			.catch(() => {
				// Best-effort: the worst outcome is being asked again next launch.
			});
	}
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
	options?: { query?: string; bounds?: WindowBounds; isMain?: boolean },
): Promise<BrowserWindow> {
	const bounds = options?.bounds;
	const windowSlot = nextWindowSlot++;
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
	if (options?.isMain ?? !options?.query) {
		mainWindowIds.add(window.id);
	}

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

	// Minimise-to-tray intercepts the window's own close, not window-all-closed: by the time
	// window-all-closed fires the window is already destroyed, too late to hide it instead
	// (§8, §13 Epic 10). Only the *last* window minimises — §13 Epic 10 is explicit that this
	// setting governs what happens when the last window closes, not every window along the
	// way. Closing one of several open windows (a popped-out compose window, say) while others
	// remain is an ordinary close and must behave like one.
	window.on("close", (event) => {
		if (
			closeBehavior === "MinimizeToTray" &&
			!quitting &&
			BrowserWindow.getAllWindows().length === 1
		) {
			event.preventDefault();
			window.hide();
			ensureTray();
		}
	});

	// Otherwise a stale entry would keep claiming this draft is still open here after the
	// window that reported it is gone, permanently blocking a real re-open of it elsewhere.
	window.on("closed", () => {
		draftWindows.delete(window.id);
		mainWindowIds.delete(window.id);
		notificationReadyWindowIds.delete(window.id);
	});

	window.once("ready-to-show", () => window.show());
	window.webContents.on("did-start-loading", () =>
		notificationReadyWindowIds.delete(window.id),
	);
	window.webContents.on("did-fail-load", (_e, code, description, url) =>
		console.error(`load failed ${code} ${description} ${url}`),
	);
	window.webContents.on("render-process-gone", (_e, details) => {
		notificationReadyWindowIds.delete(window.id);
		console.error(`renderer gone: ${details.reason}`);
	});

	// Without this a renderer failure is invisible: the window simply shows nothing, and the
	// main process's log stays clean while the app is broken.
	window.webContents.on("console-message", (event) => {
		console.info(`[renderer] ${event.message}`);
	});
	// Loaded over http from the backend rather than from disk: same-origin is what lets one
	// httpOnly cookie authenticate documents, assets, fetches and the WebSocket handshake
	// alike, and keeps the launch token out of every URL. The query string only ever picks a
	// view within that same document — never a different origin.
	const url = new URL(`${origin}/`);
	url.search = options?.query ?? "";
	url.searchParams.set("windowSlot", String(windowSlot));
	await window.loadURL(url.toString());
	return window;
}

async function navigateNotificationClick(
	origin: string,
	clicked: NotificationClicked,
): Promise<void> {
	const focused = BrowserWindow.getFocusedWindow();
	const target = notificationTargetWindow(
		notificationReadyWindowIds,
		focused?.id,
		(id) => {
			const candidate = BrowserWindow.fromId(id);
			return candidate && !candidate.isDestroyed() ? candidate : undefined;
		},
	);

	if (!target) {
		const query = new URLSearchParams({
			notification: clicked.notificationId,
			account: clicked.accountId,
		});
		const opened = await createWindow(origin, {
			query: query.toString(),
			isMain: true,
		});
		trackBoundsPersistence(opened, origin);
		return;
	}

	if (target.isMinimized()) target.restore();
	target.show();
	target.focus();
	target.webContents.send(notificationClickedChannel, clicked);
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

function isGuid(value: unknown): value is string {
	return typeof value === "string" && guid.test(value);
}

function isNotificationRequest(value: unknown): value is NotificationRequest {
	if (typeof value !== "object" || value === null) return false;
	const candidate = value as Record<string, unknown>;
	return (
		isGuid(candidate.id) &&
		isGuid(candidate.accountId) &&
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
