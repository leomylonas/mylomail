/**
 * Response headers for a remote image the reader has chosen to load.
 *
 * Many senders host their email images on a site that sends
 * `Cross-Origin-Resource-Policy: same-origin`. That is meant to stop other sites' pages reading
 * the resource, and Chromium enforces it by refusing the load outright, so the image shows as
 * broken in this app though it loads in a webmail client (which fetches images through its own
 * server). The message frame cannot run script or read pixels back, so the policy protects
 * nothing here, and dropping it is what lets an image the user allowed actually appear.
 *
 * Applied to image responses only, and never to the app's own (loopback) origin.
 */
export function relaxRemoteImageHeaders(
	resourceType: string,
	url: string,
	headers: Record<string, string[]>,
): Record<string, string[]> | null {
	if (resourceType !== "image" || isLoopback(url)) return null;

	const relaxed: Record<string, string[]> = {};
	let changed = false;
	for (const [name, value] of Object.entries(headers)) {
		if (name.toLowerCase() === "cross-origin-resource-policy") {
			changed = true;
			continue;
		}
		relaxed[name] = value;
	}
	return changed ? relaxed : null;
}

function isLoopback(url: string): boolean {
	try {
		const { hostname } = new URL(url);
		return (
			hostname === "127.0.0.1" ||
			hostname === "localhost" ||
			hostname === "[::1]"
		);
	} catch {
		return true;
	}
}
