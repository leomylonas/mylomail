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
export const notificationNavigationReadyChannel =
	"notification:navigation-ready";
export const openWindowChannel = "window:open";
export const pickExportFolderChannel = "export:pick-folder";
export const updateCloseBehaviorChannel =
	"shell-settings:close-behavior-changed";
export const reportDraftStateChannel = "draft:state-changed";
export const focusDraftWindowChannel = "draft:focus-if-open";

/**
 * What the renderer hands the shell to show a native OS notification (§13 Epic 9).
 *
 * The durable notification and account ids are sufficient for display/click routing. Message,
 * mailbox, subject and sender context are resolved from the backend when the user clicks, rather
 * than frozen into an OS payload that may predate staged replay or a provider-side move.
 */
export interface NotificationRequest {
	id: string;
	accountId: string;
	title: string;
	body: string;
}

/** The durable identity a native-notification click hands back to one main renderer window. */
export interface NotificationClicked {
	notificationId: string;
	accountId: string;
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
