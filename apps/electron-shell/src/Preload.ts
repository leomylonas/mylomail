import { contextBridge, ipcRenderer } from "electron";
import {
	backendConnectionChannel,
	notificationClickedChannel,
	openAttachmentChannel,
	showNotificationChannel,
	type BackendConnection,
	type NotificationRequest,
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
	onClicked: (callback: (messageId: string) => void): (() => void) => {
		const handler = (_event: Electron.IpcRendererEvent, messageId: string) =>
			callback(messageId);
		ipcRenderer.on(notificationClickedChannel, handler);
		return () => ipcRenderer.off(notificationClickedChannel, handler);
	},
});
