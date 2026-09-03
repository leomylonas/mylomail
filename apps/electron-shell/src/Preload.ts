import { contextBridge, ipcRenderer } from "electron";
import {
	backendConnectionChannel,
	notificationClickedChannel,
	openAttachmentChannel,
	openWindowChannel,
	pickExportFolderChannel,
	showNotificationChannel,
	updateCloseBehaviorChannel,
	type BackendConnection,
	type NotificationClicked,
	type NotificationRequest,
	type OpenWindowRequest,
} from "@mylomail/electron-shell/BackendConnection";

/**
 * Exposed through the context bridge, and fetched over IPC rather than read from argv.
 *
 * A command line is world-readable — on Linux any local process can read `/proc/<pid>/cmdline`
 * — and the launch token exists precisely to stop another local process talking to the
 * backend. Putting it in argv would publish the thing it protects.
 */
contextBridge.exposeInMainWorld("backend", {
	connect: (): Promise<BackendConnection> =>
		ipcRenderer.invoke(backendConnectionChannel) as Promise<BackendConnection>,
	openAttachment: (messageId: string, attachmentId: string): Promise<string> =>
		ipcRenderer.invoke(
			openAttachmentChannel,
			messageId,
			attachmentId,
		) as Promise<string>,
});

/**
 * Dispatch is the shell's job, not the renderer's (§13 Epic 9) — the renderer only relays
 * what the hub told it and listens for a click coming back.
 */
contextBridge.exposeInMainWorld("notifications", {
	show: (request: NotificationRequest): Promise<void> =>
		ipcRenderer.invoke(showNotificationChannel, request) as Promise<void>,
	onClicked: (
		callback: (clicked: NotificationClicked) => void,
	): (() => void) => {
		const handler = (
			_event: Electron.IpcRendererEvent,
			clicked: NotificationClicked,
		) => callback(clicked);
		ipcRenderer.on(notificationClickedChannel, handler);
		return () => ipcRenderer.off(notificationClickedChannel, handler);
	},
});

/**
 * Opening a window is a main-process decision (§13 Epic 10): the renderer asks for one by
 * shape, and the shell decides bounds, offset, and which existing window it opened relative to.
 */
contextBridge.exposeInMainWorld("windows", {
	open: (query?: string): Promise<void> =>
		ipcRenderer.invoke(openWindowChannel, {
			query,
		} satisfies OpenWindowRequest) as Promise<void>,
});

/**
 * The renderer has no filesystem access (§13 Export) — picking where a bulk export writes to
 * is a native folder-picker dialog, a main-process capability like attachment opening above.
 */
contextBridge.exposeInMainWorld("dialogs", {
	pickExportFolder: (): Promise<string | null> =>
		ipcRenderer.invoke(pickExportFolderChannel) as Promise<string | null>,
});

/**
 * `CloseBehavior` is main-process state (§13 Epic 10) read from `AppSettings` once at
 * startup — a change saved through ShellSettings only reaches this run if the renderer
 * pushes it across after the write succeeds, since nothing else re-reads it mid-session.
 */
contextBridge.exposeInMainWorld("shellSettings", {
	closeBehaviorChanged: (value: number): Promise<void> =>
		ipcRenderer.invoke(updateCloseBehaviorChannel, value) as Promise<void>,
});
