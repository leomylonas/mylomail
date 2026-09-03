/**
 * The contract between the main process and the renderer.
 *
 * It lives apart from both because each side imports things the other must not: the preload
 * needs `contextBridge`, which does not exist in the main process, and importing the preload
 * from main pulled that into the main bundle and failed at load.
 */

/** The channel the renderer uses to learn where the backend is. */
export const backendConnectionChannel = "backend:connection";
export const openAttachmentChannel = "attachment:open";
export const showNotificationChannel = "notification:show";
export const notificationClickedChannel = "notification:clicked";
export const openWindowChannel = "window:open";
export const pickExportFolderChannel = "export:pick-folder";
export const updateCloseBehaviorChannel =
	"shell-settings:close-behavior-changed";

/**
 * What the renderer hands the shell to show a native OS notification (§13 Epic 9).
 *
 * `messageId` is null for one recorded from a still-staged, not-yet-replayed change-stream
 * page — there is no local message to navigate to yet. The notification still shows; a click
 * on it just cannot navigate anywhere until replay catches up (§3).
 */
export interface NotificationRequest {
	id: string;
	title: string;
	body: string;
	messageId: string | null;
}

/**
 * What a notification click hands back to the renderer. `messageId` repeats the
 * `NotificationRequest` at the moment it was shown — the shell keeps no state of its own — so
 * when it is null the renderer is the one that asks the backend to resolve
 * `notificationId` on demand rather than the click doing nothing (§3).
 */
export interface NotificationClicked {
	notificationId: string;
	messageId: string | null;
}

/**
 * Where the backend is listening.
 *
 * <b>The launch token is deliberately absent.</b> The renderer is served from the backend's
 * own origin and authenticates with an httpOnly cookie the main process sets before anything
 * loads, so the token never enters the renderer process — and cannot be read by script there
 * even if something in the page tried (§9).
 */
export interface BackendConnection {
	origin: string;
}

/**
 * Requests an additional window (§13 Epic 10). `query` becomes the new window's URL query
 * string — empty for a plain independent main window, `message=<id>` to open a single message,
 * `compose=<draftId>&account=<accountId>` to pop a draft out of the main window that opened it.
 * A relative query string, never a full URL: the shell decides the origin, so a window can never
 * be pointed anywhere but the backend it already trusts.
 */
export interface OpenWindowRequest {
	query?: string;
}
