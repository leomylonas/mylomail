/**
 * Reads the watcher's current-state file and prints the smallest useful thing.
 *
 * Contract with the caller:
 *   exit 0 — verified green
 *   exit 1 — real failures, listed
 *   exit 2 — NOT VERIFIED (watcher dead, or status predates the current tree)
 *
 * Exit 2 exists because the dangerous failure mode of this pattern is reading a stale
 * green result for code that does not compile. "Not verified" is never success.
 */

import { readFileSync } from "node:fs";
import {
	type StatusFile,
	type ToolName,
	STATUS_PATH,
	generation,
} from "./tooling.ts";

const ROOT = process.cwd();
const STALE_HEARTBEAT_MS = 20_000;

function read(): StatusFile | null {
	try {
		return JSON.parse(readFileSync(STATUS_PATH(ROOT), "utf8")) as StatusFile;
	} catch {
		return null;
	}
}

function report(status: StatusFile): void {
	for (const name of Object.keys(status.tools) as ToolName[]) {
		const tool = status.tools[name];

		if (tool.status === "ok") continue;

		if (tool.status === "pending" || tool.status === "running") {
			console.log(`… ${name}: still compiling`);
			continue;
		}

		console.log(`✗ ${name} — ${tool.total} error(s):`);
		for (const d of tool.shown) {
			const repeated = d.occurrences > 1 ? ` (×${d.occurrences})` : "";
			console.log(`  ${d.file}:${d.line} ${d.code} ${d.message}${repeated}`);
		}
		if (tool.suppressed > 0) {
			console.log(`  … +${tool.suppressed} more distinct error(s)`);
		}
	}
}

function main(): number {
	const status = read();

	if (status === null) {
		console.log(
			"NOT VERIFIED — watcher not running. Start it with `pnpm watch`,",
		);
		console.log("or run `pnpm check:fast` to check directly.");
		return 2;
	}

	const age = Date.now() - status.heartbeat;
	if (age > STALE_HEARTBEAT_MS) {
		console.log(
			`NOT VERIFIED — watcher last responded ${Math.round(age / 1000)}s ago (likely dead).`,
		);
		return 2;
	}

	if (status.generation !== generation(ROOT)) {
		console.log(
			"STALE — files changed since last compile; recompiling. Retry shortly.",
		);
		return 2;
	}

	if (status.ok) {
		console.log("✓ tsc · eslint · dotnet — clean");
		return 0;
	}

	report(status);
	return 1;
}

process.exit(main());
