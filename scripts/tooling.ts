/**
 * Shared contract between the watcher and its readers.
 *
 * The status file is CURRENT STATE, not a log: it is overwritten atomically and never
 * appended to. An append-only log costs the reader more on every read, which defeats
 * the purpose of having a watcher at all.
 */

import { readdirSync, statSync } from "node:fs";
import { join, extname, relative } from "node:path";
import { createHash } from "node:crypto";

export type ToolName = "tsc" | "eslint" | "dotnet";

export type ToolStatus = "pending" | "running" | "ok" | "error";

export interface Diagnostic {
	file: string;
	line: number;
	/** Error code (TS2304, CS0246, an ESLint rule name) — the deduplication key. */
	code: string;
	message: string;
}

export interface DedupedDiagnostic extends Diagnostic {
	occurrences: number;
}

export interface ToolState {
	status: ToolStatus;
	shown: DedupedDiagnostic[];
	/** Distinct diagnostics beyond the display cap. */
	suppressed: number;
	/** Total diagnostics before deduplication. */
	total: number;
}

export interface StatusFile {
	ok: boolean;
	/** Hash over source mtimes. Lets a reader detect that files changed since this ran. */
	generation: string;
	/** Epoch ms. Lets a reader detect a dead watcher. */
	heartbeat: number;
	tools: Record<ToolName, ToolState>;
}

export const STATUS_PATH = (root: string): string => join(root, ".dev", "status.json");

export const MAX_SHOWN = 15;

const SOURCE_EXTENSIONS = new Set([".ts", ".tsx", ".cs", ".css", ".json"]);
const SKIP_DIRECTORIES = new Set(["node_modules", "bin", "obj", "dist", ".git", ".dev"]);

/**
 * Cheap proxy for "has the tree changed since this result was produced".
 *
 * This exists because the dangerous failure mode of a watcher-based setup is reading a
 * stale green result for code that does not compile. That is a correctness failure, not
 * an inconvenience, so every status read compares generations.
 */
export function generation(root: string): string {
	const hash = createHash("sha1");

	const walk = (dir: string): void => {
		let entries;
		try {
			entries = readdirSync(dir, { withFileTypes: true });
		} catch {
			return;
		}
		for (const entry of entries) {
			if (SKIP_DIRECTORIES.has(entry.name)) continue;
			const path = join(dir, entry.name);
			if (entry.isDirectory()) {
				walk(path);
			} else if (SOURCE_EXTENSIONS.has(extname(entry.name))) {
				hash.update(path + statSync(path).mtimeMs);
			}
		}
	};

	walk(root);
	return hash.digest("hex").slice(0, 12);
}

/**
 * Collapse repetition. One missing import produces forty errors; a reader needs the
 * distinct causes and a count, not all forty. Done once here rather than in every reader.
 */
export function dedupe(diagnostics: Diagnostic[]): Omit<ToolState, "status"> {
	const byCode = new Map<string, DedupedDiagnostic>();

	for (const diagnostic of diagnostics) {
		const key = diagnostic.code || diagnostic.message.slice(0, 40);
		const existing = byCode.get(key);
		if (existing) existing.occurrences++;
		else byCode.set(key, { ...diagnostic, occurrences: 1 });
	}

	const all = [...byCode.values()];
	return {
		shown: all.slice(0, MAX_SHOWN),
		suppressed: Math.max(0, all.length - MAX_SHOWN),
		total: diagnostics.length,
	};
}

export function relativePath(root: string, path: string): string {
	try {
		return relative(root, path);
	} catch {
		return path;
	}
}

/** Output patterns. Verify against your installed toolchain — MSBuild's varies by version. */
export const PATTERNS = {
	/** tsc --pretty false: path(line,col): error TS2304: message */
	tsc: /^(.+?)\((\d+),(\d+)\):\s+error\s+(TS\d+):\s+(.*)$/,
	/** eslint -f unix: path:line:col: message [rule] */
	eslint: /^(.+?):(\d+):(\d+):\s+(.*?)\s+\[(.+?)\]$/,
	/** msbuild: path(line,col): error CS0246: message [project] */
	dotnet: /^(.+?)\((\d+),(\d+)\):\s+error\s+(CS\d+):\s+(.*?)(\s+\[.*\])?$/,
	xunitFailure: /^\s*(?:Failed|X)\s+(.+?)\s*(?:\[.*\])?$/,
	vitestFailure: /^\s*(?:FAIL|×)\s+(.+)$/,
	dotnetPassed: /Passed!.*Passed:\s*(\d+)/,
	vitestPassed: /Tests\s+(\d+)\s+passed/,
} as const;

export const isWindows = process.platform === "win32";
