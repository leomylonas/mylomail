import { waitForBackendOriginHealth } from "@mylomail/electron-shell/BackendHealthProbe";

export interface AttachedBackend {
	origin: string;
	launchToken: string;
}

export interface BackendAttachmentOptions {
	backendUrl?: string;
	launchToken?: string;
	waitUntilReady?(origin: string, launchToken: string): Promise<void>;
}

/**
 * Connects the shell to an independently owned backend for debugger and isolated-E2E use.
 * The shell never starts, restarts, or terminates that process. Both processes must receive
 * the same launch token; attach mode is not a bypass around loopback authentication.
 */
export async function attachBackend(
	options: BackendAttachmentOptions,
): Promise<AttachedBackend> {
	const origin = parseBackendOrigin(options.backendUrl);
	const launchToken = options.launchToken?.trim();
	if (!launchToken) {
		throw new Error(
			"MYLOMAIL_LAUNCH_TOKEN is required when ELECTRON_BACKEND_MODE=attach.",
		);
	}

	await (options.waitUntilReady ?? waitForBackendOriginHealth)(
		origin,
		launchToken,
	);
	return { origin, launchToken };
}

function parseBackendOrigin(value: string | undefined): string {
	if (!value?.trim()) {
		throw new Error(
			"BACKEND_URL is required when ELECTRON_BACKEND_MODE=attach.",
		);
	}

	let parsed: URL;
	try {
		parsed = new URL(value);
	} catch {
		throw new Error("BACKEND_URL must be an absolute loopback HTTP URL.");
	}
	if (
		parsed.protocol !== "http:" ||
		parsed.hostname !== "127.0.0.1" ||
		!parsed.port ||
		parsed.username ||
		parsed.password ||
		(parsed.pathname !== "/" && parsed.pathname !== "") ||
		parsed.search ||
		parsed.hash
	) {
		throw new Error(
			"BACKEND_URL must be an origin such as http://127.0.0.1:5123.",
		);
	}

	const port = Number.parseInt(parsed.port, 10);
	if (!Number.isInteger(port) || port < 1 || port > 65_535) {
		throw new Error("BACKEND_URL must contain a valid loopback port.");
	}
	return parsed.origin;
}
