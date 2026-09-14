/**
 * Full verification: build, unit tests, invariant tests. One line on success.
 *
 * A green run of six tools should cost roughly ten tokens, and a failing run should
 * cost in proportion to the failure — not in proportion to how verbose the tools are.
 *
 *   pnpm check       formatting + build + unit + invariant
 *   pnpm check:fast  the same checks the watcher does, run directly (no watcher needed)
 *   pnpm check:platform  build + tests on non-Linux hosts
 *   pnpm check:deep  + fault injection + provider conformance (slow, explicit only)
 *   pnpm check:native-credentials  one real host credential-store round trip
 *   pnpm check:live-providers [gmail|graph] [mail|calendar|contacts]
 */

import { existsSync } from "node:fs";
import { join } from "node:path";
import { loadEnvFile } from "node:process";
import { spawnSync } from "node:child_process";
import { PATTERNS, MAX_SHOWN, isWindows, relativePath } from "./tooling.ts";

type Mode = "full" | "fast" | "deep" | "platform" | "native" | "live";
type LiveProvider = "all" | "gmail" | "graph";
type LiveArea = "all" | "mail" | "calendar" | "contacts";

const mode = (process.argv[2] ?? "full") as Mode;
const liveProvider = (process.argv[3] ?? "all") as LiveProvider;
const liveArea = (process.argv[4] ?? "all") as LiveArea;
const ROOT = process.cwd();

if (
	mode === "live" &&
	!(["all", "gmail", "graph"] as const).includes(liveProvider)
) {
	console.error("Live provider must be one of: all, gmail, graph.");
	process.exit(2);
}
if (
	mode === "live" &&
	!(["all", "mail", "calendar", "contacts"] as const).includes(liveArea)
) {
	console.error(
		"Live provider area must be one of: all, mail, calendar, contacts.",
	);
	process.exit(2);
}

if (mode === "live") {
	const environmentPath = join(ROOT, ".dev", "provider-test.env");
	if (!existsSync(environmentPath)) {
		console.error(".dev/provider-test.env does not exist.");
		process.exit(2);
	}
	loadEnvFile(environmentPath);
	const missing = [
		...(liveProvider !== "graph"
			? ["GMAIL_CLIENT_ID", "GMAIL_CLIENT_SECRET", "GMAIL_TOKEN_CACHE_BASE64"]
			: []),
		...(liveProvider !== "gmail"
			? ["GRAPH_CLIENT_ID", "GRAPH_TENANT_ID", "GRAPH_TOKEN_CACHE_BASE64"]
			: []),
	].filter((name) => !process.env[name]);
	if (missing.length > 0) {
		console.error(
			`Missing provider test credentials: ${missing.join(", ")}. Run pnpm provider:authorize for each provider.`,
		);
		process.exit(2);
	}
}

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
	resultsFilePrefix: string,
	noBuild = true,
): Pick<Step, "command" | "args" | "parse"> => ({
	command: "dotnet",
	args: [
		"test",
		...(process.env.MYLOMAIL_TEST_PROJECT
			? [process.env.MYLOMAIL_TEST_PROJECT]
			: []),
		"--nologo",
		...(noBuild ? ["--no-build"] : []),
		"--logger",
		"console;verbosity=quiet",
		"--filter",
		filter,
		...(process.env.MYLOMAIL_TEST_RESULTS_DIRECTORY
			? [
					"--logger",
					`trx;LogFilePrefix=${resultsFilePrefix}`,
					"--results-directory",
					process.env.MYLOMAIL_TEST_RESULTS_DIRECTORY,
				]
			: []),
	],
	parse: testParser(PATTERNS.xunitFailure),
});

const steps: Step[] = [];

if (mode !== "native") {
	if (mode !== "platform") {
		steps.push({
			name: "format",
			command: "pnpm",
			args: ["format:check"],
			parse: () => null,
			fatal: true,
		});
	}

	steps.push({
		name: "tsc",
		command: "npx",
		args: ["tsc", "--noEmit", "--pretty", "false"],
		parse: compileParser(PATTERNS.tsc),
		fatal: true,
	});

	if (mode !== "platform") {
		steps.push(
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
				// CSS Modules only; the rule set enforces camelCase class names so they read as
				// `styles.messageRow` from TypeScript.
				name: "stylelint",
				command: "npx",
				args: [
					"stylelint",
					"**/*.css",
					"--allow-empty-input",
					"--formatter",
					"unix",
				],
				parse: (line) => {
					const m = PATTERNS.stylelint.exec(line);
					if (!m) return null;
					return {
						key: m[5],
						text: `${relativePath(ROOT, m[1])}:${m[2]} ${m[5]} ${m[4]}`,
					};
				},
			},
		);
	}

	steps.push({
		name: "build",
		command: "dotnet",
		args: ["build", "--nologo", "-tl:off", "-clp:ErrorsOnly"],
		parse: compileParser(PATTERNS.dotnet),
		fatal: true,
	});
}

if (mode !== "fast" && mode !== "native" && mode !== "live") {
	steps.push({ name: "tests", ...dotnetTest("Category!=Deep", "tests") });
	steps.push({
		name: "vitest",
		command: "npx",
		args: ["vitest", "run", "--reporter=dot"],
		parse: testParser(PATTERNS.vitestFailure),
	});
}

if (mode === "native") {
	// Explicit because this writes to the host's real credential store. Keep it
	// separate from the broad Deep suite: native tag runners do not provide
	// provider credentials or the local conformance containers.
	steps.push({
		name: "native-credentials",
		...dotnetTest(
			"FullyQualifiedName~NativeCredentialStoreLiveTests",
			"native-credentials",
			false,
		),
	});
}

if (mode === "live") {
	steps.push({
		name: "live-providers",
		command: "dotnet",
		args: [
			"test",
			"server/MyloMail.Api.IntegrationTests/MyloMail.Api.IntegrationTests.csproj",
			"--nologo",
			"--no-build",
			"--logger",
			"console;verbosity=quiet",
			...(process.env.MYLOMAIL_TEST_RESULTS_DIRECTORY
				? [
						"--logger",
						`trx;LogFilePrefix=live-${liveProvider}-${liveArea}`,
						"--results-directory",
						process.env.MYLOMAIL_TEST_RESULTS_DIRECTORY,
					]
				: []),
			"--filter",
			[
				"Category=LiveProvider",
				liveProvider === "all"
					? null
					: `Provider=${liveProvider === "gmail" ? "Gmail" : "Graph"}`,
				liveArea === "all"
					? null
					: `Area=${liveArea[0].toUpperCase()}${liveArea.slice(1)}`,
			]
				.filter((part) => part !== null)
				.join("&"),
		],
		parse: testParser(PATTERNS.xunitFailure),
	});
}

if (mode === "deep") {
	// Slow by design: spawns and kills processes, exercises all three providers across
	// all three IMAP capability tiers. Never run this in the inner loop.
	steps.push({
		name: "conformance",
		...dotnetTest(
			process.env.MYLOMAIL_CONFORMANCE_FILTER ?? "Category=Conformance",
			"conformance",
		),
	});
	steps.push({
		name: "fault-injection",
		...dotnetTest("Category=FaultInjection", "fault-injection"),
	});

	steps.push({
		name: "electron",
		command: "pnpm",
		args: ["build:electron"],
		parse: compileParser(PATTERNS.tsc),
		fatal: true,
	});

	// The product, driven as a user drives it, against the local IMAP matrix. It asks the
	// mail server what happened rather than asking the app what it believes — which is the
	// only reason it catches a mutation that is recorded locally and never sent.
	steps.push({
		name: "e2e",
		command: "npx",
		args: [
			"playwright",
			"test",
			"--config",
			"tests/renderer.e2e/Playwright.config.ts",
			"--reporter=list",
		],
		parse: (line) => {
			const match = /^\s*✘\s+\d+\s+(.+?)(?:\s+\(\d+.*\))?$/.exec(line);
			return match ? { key: match[1], text: match[1] } : null;
		},
	});
	// Mutation testing over the mutation and sync cores. This is the mechanical form of the
	// discrimination check in docs/skills/fault-injection.md: a surviving mutant in a guard
	// clause means a test passes against the bug it was written to catch. Two scenarios in
	// this repository did exactly that, and neither was found by running the suite.
	//
	// Only survivors are parsed as failures. The reported mutation score is deliberately
	// ignored: Stryker counts a timed-out mutant as killed, so a hanging suite scores well.
	steps.push({
		name: "mutants",
		command: "dotnet",
		args: ["stryker", "--solution", "MyloMail.sln", "--reporter", "progress"],
		parse: (line) => {
			const m = PATTERNS.strykerSurvived.exec(line);
			return m ? { key: m[1], text: `survived: ${m[1]}` } : null;
		},
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
