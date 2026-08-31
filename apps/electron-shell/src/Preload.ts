import { contextBridge, ipcRenderer } from "electron";
import {
	backendConnectionChannel,
	type BackendConnection,
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
});
