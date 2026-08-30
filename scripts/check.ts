/**
 * Full verification: build, unit tests, invariant tests. One line on success.
 *
 * A green run of six tools should cost roughly ten tokens, and a failing run should
 * cost in proportion to the failure — not in proportion to how verbose the tools are.
 *
 *   pnpm check       formatting + build + unit + invariant
 *   pnpm check:fast  the same checks the watcher does, run directly (no watcher needed)
 *   pnpm check:deep  + fault injection + provider conformance (slow, explicit only)
 */

import { spawnSync } from "node:child_process";
import { PATTERNS, MAX_SHOWN, isWindows, relativePath } from "./tooling.ts";

type Mode = "full" | "fast" | "deep";

const mode = (process.argv[2] ?? "full") as Mode;
const ROOT = process.cwd();

interface Failure {
	/** Deduplication key: error code, or test name. */
	key: string;
	text: string;
}

interface Step {
	name: string;
	command: string;
	args: string[];
	parse: (line: string) => Failure | null;
	/** Later steps are meaningless once this fails, so stop rather than cascading. */
	fatal?: boolean;
}

const compileParser =
	(pattern: RegExp) =>
	(line: string): Failure | null => {
		const m = pattern.exec(line);
		if (!m) return null;
		return {
			key: m[4],
			text: `${relativePath(ROOT, m[1])}:${m[2]} ${m[4]} ${m[5]}`,
		};
	};

const testParser =
	(pattern: RegExp) =>
	(line: string): Failure | null => {
		const m = pattern.exec(line);
		return m ? { key: m[1], text: m[1] } : null;
	};

const dotnetTest = (
	filter: string,
): Pick<Step, "command" | "args" | "parse"> => ({
	command: "dotnet",
	args: [
		"test",
		"--nologo",
		"--no-build",
		"--logger",
		"console;verbosity=quiet",
		"--filter",
		filter,
	],
	parse: testParser(PATTERNS.xunitFailure),
});

const steps: Step[] = [
	{
		name: "format",
		command: "pnpm",
		args: ["format:check"],
		parse: () => null,
		fatal: true,
	},
	{
		name: "tsc",
		command: "npx",
		args: ["tsc", "--noEmit", "--pretty", "false"],
		parse: compileParser(PATTERNS.tsc),
		fatal: true,
	},
	{
		name: "eslint",
		command: "npx",
		args: ["eslint", ".", "--quiet", "-f", "unix"],
		parse: (line) => {
			const m = PATTERNS.eslint.exec(line);
			if (!m) return null;
			return {
				key: m[5],
				text: `${relativePath(ROOT, m[1])}:${m[2]} ${m[5]} ${m[4]}`,
			};
		},
	},
	{
		name: "build",
		command: "dotnet",
		args: ["build", "--nologo", "-tl:off", "-clp:ErrorsOnly"],
		parse: compileParser(PATTERNS.dotnet),
		fatal: true,
	},
];

if (mode !== "fast") {
	steps.push({ name: "tests", ...dotnetTest("Category!=Deep") });
	steps.push({
		name: "vitest",
		command: "npx",
		args: ["vitest", "run", "--reporter=dot"],
		parse: testParser(PATTERNS.vitestFailure),
	});
}

if (mode === "deep") {
	// Slow by design: spawns and kills processes, exercises all three providers across
	// all three IMAP capability tiers. Never run this in the inner loop.
	steps.push({ name: "conformance", ...dotnetTest("Category=Conformance") });
	steps.push({
		name: "fault-injection",
		...dotnetTest("Category=FaultInjection"),
	});
}

let failed = false;
const summary: string[] = [];

for (const step of steps) {
	const result = spawnSync(step.command, step.args, {
		cwd: ROOT,
		encoding: "utf8",
		shell: isWindows,
	});

	const output = `${result.stdout ?? ""}\n${result.stderr ?? ""}`;

	if (result.status === 0) {
		const passed =
			PATTERNS.dotnetPassed.exec(output)?.[1] ??
			PATTERNS.vitestPassed.exec(output)?.[1];
		summary.push(passed ? `${step.name}(${passed})` : step.name);
		continue;
	}

	failed = true;

	// Deduplicate by code or test name: one root cause often produces dozens of lines,
	// and the reader needs distinct causes plus a count.
	const seen = new Map<string, { text: string; count: number }>();
	for (const line of output.split("\n")) {
		const failure = step.parse(line);
		if (!failure) continue;
		const existing = seen.get(failure.key);
		if (existing) existing.count++;
		else seen.set(failure.key, { text: failure.text, count: 1 });
	}

	const items = [...seen.values()];
	console.log(`\n✗ ${step.name} — ${items.length} distinct failure(s)`);

	if (items.length === 0) {
		// Parser did not match: show the tail rather than dumping everything.
		console.log(output.trim().split("\n").slice(-20).join("\n"));
	} else {
		for (const item of items.slice(0, MAX_SHOWN)) {
			console.log(`  ${item.text}${item.count > 1 ? ` (×${item.count})` : ""}`);
		}
		if (items.length > MAX_SHOWN) {
			console.log(`  … +${items.length - MAX_SHOWN} more`);
		}
	}

	if (step.fatal) break;
}

if (!failed) {
	console.log(`✓ ${summary.join(" · ")}`);
	process.exit(0);
}

process.exit(1);
