import { defineConfig } from "@playwright/test";
import { fileURLToPath } from "node:url";

/**
 * End-to-end tests that drive the real Electron app.
 *
 * Serial by design: each test launches the app against one data directory and one IMAP
 * server, and two of them running at once would race over both.
 */
export default defineConfig({
	testDir: fileURLToPath(new URL(".", import.meta.url)),
	testMatch: "**/*.e2e.ts",
	workers: 1,
	fullyParallel: false,
	// The app spawns a backend, migrates a database and syncs a mailbox before anything is
	// visible, so the default five seconds is far too short to mean anything. Several specs
	// then wait on two or three server round trips in sequence — a flag reaching the server,
	// a refused mutation surfacing, a message arriving at the receiving server — each with
	// its own poll window, and the sum of those windows is what this has to clear.
	timeout: 240_000,
	expect: { timeout: 30_000 },
	reporter: [["list"]],
});
