import { useEffect, useState } from "react";
import { HubConnectionBuilder, LogLevel } from "@microsoft/signalr";
import styles from "@mylomail/renderer/Shell/BackendStatus/BackendStatus.module.css";

interface BackendConnection {
	origin: string;
}

declare global {
	interface Window {
		backend?: { connect(): Promise<BackendConnection> };
	}
}

type Probe =
	| { kind: "connecting" }
	| { kind: "connected"; status: string; accounts: number }
	| { kind: "failed"; reason: string };

/**
 * The whole renderer, for now: it proves the chain from window to backend works.
 *
 * Deliberately minimal and deliberately not Carbon — the panel and registry architecture in
 * §13 is Stage D's subject, and building a throwaway version of it here would have to be
 * unbuilt. What this does establish is the part Stage D depends on and cannot assume: that
 * the renderer can reach an authenticated backend at all.
 */
export function BackendStatus() {
	const [probe, setProbe] = useState<Probe>({ kind: "connecting" });

	useEffect(() => {
		let cancelled = false;

		const run = async () => {
			try {
				const bridge = window.backend;
				if (!bridge) throw new Error("preload bridge unavailable");

				// The origin is still fetched so the renderer fails loudly if the shell has not
				// wired it up, but requests are relative: the page is served by the backend.
				await bridge.connect();
				const health = await request("/health");
				const accounts = await request("/accounts");
				await openHub();

				if (!cancelled)
					console.info(
						`backend ${JSON.stringify(health)}; accounts=${JSON.stringify(accounts)}`,
					);
				if (!cancelled)
					setProbe({
						kind: "connected",
						status: (health as { status?: string }).status ?? "unknown",
						accounts: Array.isArray(accounts) ? accounts.length : 0,
					});
			} catch (error) {
				if (!cancelled) setProbe({ kind: "failed", reason: String(error) });
			}
		};

		void run();
		return () => {
			cancelled = true;
		};
	}, []);

	return (
		<main className={styles.panel}>
			<h1>MyloMail</h1>
			<p className={styles.state}>{describe(probe)}</p>
		</main>
	);
}

function describe(probe: Probe): string {
	switch (probe.kind) {
		case "connecting":
			return "Connecting to the backend…";
		case "connected":
			return `Backend ${probe.status} — ${probe.accounts} account(s).`;
		case "failed":
			return `Could not reach the backend: ${probe.reason}`;
	}
}

/**
 * Opens the hub connection.
 *
 * A WebSocket handshake cannot carry custom headers, which is normally why SignalR appends an
 * access token to the URL. It does not here: the page is same-origin with the backend, so the
 * handshake sends the httpOnly launch cookie instead and no token appears in a URL (§9).
 *
 * Automatic reconnect is deliberate, and on reconnect the client must invalidate and refetch
 * its active queries: while disconnected it missed every event, and pending mutations alone
 * cannot repair a stale cache (§7). There are no queries to invalidate yet, so this logs
 * where that will go.
 */
async function openHub(): Promise<void> {
	const hub = new HubConnectionBuilder()
		// No access-token factory: the page is served from the backend's origin, so the
		// handshake carries the launch cookie by itself and the token stays out of the URL.
		.withUrl("/hub")
		.withAutomaticReconnect()
		// SignalR logs the negotiated URL at Information, and that URL carries the access
		// token as a query parameter — so the default level writes the launch token into the
		// console verbatim.
		.configureLogging(LogLevel.Warning)
		.build();

	hub.onreconnected(() => {
		console.info("hub reconnected — active queries must be refetched here");
	});

	hub.on("SyncProgress", (progress: unknown) => {
		console.info(`hub SyncProgress ${JSON.stringify(progress)}`);
	});

	await hub.start();
	console.info("hub connected");
}

async function request(path: string): Promise<unknown> {
	// Same-origin, so the launch cookie goes along and no header is needed.
	const response = await fetch(path);
	if (!response.ok) throw new Error(`${path} responded ${response.status}`);
	return response.json();
}
