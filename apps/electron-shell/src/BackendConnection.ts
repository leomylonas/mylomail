/**
 * The contract between the main process and the renderer.
 *
 * It lives apart from both because each side imports things the other must not: the preload
 * needs `contextBridge`, which does not exist in the main process, and importing the preload
 * from main pulled that into the main bundle and failed at load.
 */

/** The channel the renderer uses to learn where the backend is. */
export const backendConnectionChannel = "backend:connection";

/** Where the backend is listening, and the token every request to it must carry (§9). */
export interface BackendConnection {
	origin: string;
	launchToken: string;
}
