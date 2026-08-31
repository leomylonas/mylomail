import { useEffect, useState } from "react";
import styles from "@mylomail/renderer/Shell/BackendStatus/BackendStatus.module.css";

interface BackendConnection {
	origin: string;
	launchToken: string;
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

				const connection = await bridge.connect();
				const health = await request(connection, "/health");
				const accounts = await request(connection, "/accounts");

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

async function request(
	connection: BackendConnection,
	path: string,
): Promise<unknown> {
	const response = await fetch(`${connection.origin}${path}`, {
		headers: { Authorization: `Bearer ${connection.launchToken}` },
	});
	if (!response.ok) throw new Error(`${path} responded ${response.status}`);
	return response.json();
}
