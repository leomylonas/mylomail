/**
 * Shared formatting wrapper. Keeps the successful path to one line and caps failures,
 * so formatting remains as cheap to inspect as the other repository checks.
 */

import { spawnSync } from "node:child_process";
import { MAX_SHOWN, PATTERNS, isWindows } from "./tooling.ts";

const mode = process.argv[2] === "write" ? "write" : "check";
const root = process.cwd();
const paths = [
	"apps",
	"packages",
	"scripts",
	"tests",
	".github/workflows",
	"package.json",
	"pnpm-workspace.yaml",
	"tsconfig.json",
	"tsconfig.scripts.json",
	"eslint.config.js",
	"vitest.config.ts",
	"global.json",
	".prettierrc.json",
];

const steps = [
	{
		name: "prettier",
		command: "npx",
		args: ["prettier", `--${mode}`, "--ignore-unknown", ...paths],
	},
	{
		name: "dotnet-format",
		command: "dotnet",
		args: [
			"format",
			"MyloMail.sln",
			...(mode === "check" ? ["--verify-no-changes"] : []),
			"--no-restore",
		],
	},
];

interface Failure {
	key: string;
	text: string;
}

const parseFailure = (line: string): Failure | null => {
	const dotnet = PATTERNS.dotnet.exec(line);
	if (dotnet) return { key: dotnet[4], text: line.trim() };

	const prettier = /^\[warn\]\s+(.+)$/.exec(line);
	if (prettier) return { key: prettier[1], text: line.trim() };

	return /\b(?:error|failed|exception)\b/i.test(line)
		? { key: line.trim(), text: line.trim() }
		: null;
};

for (const step of steps) {
	const result = spawnSync(step.command, step.args, {
		cwd: root,
		encoding: "utf8",
		shell: isWindows,
	});

	if (result.status === 0) continue;

	const output = `${result.stdout ?? ""}\n${result.stderr ?? ""}`;
	const failures = new Map<string, { text: string; count: number }>();

	for (const line of output.split("\n")) {
		const failure = parseFailure(line);
		if (!failure) continue;
		const existing = failures.get(failure.key);
		if (existing) existing.count++;
		else failures.set(failure.key, { text: failure.text, count: 1 });
	}

	const items = [...failures.values()];
	console.log(`✗ ${step.name} — ${items.length} distinct failure(s)`);

	if (items.length === 0) {
		console.log(output.trim().split("\n").slice(-20).join("\n"));
	} else {
		for (const item of items.slice(0, MAX_SHOWN)) {
			console.log(`  ${item.text}${item.count > 1 ? ` (×${item.count})` : ""}`);
		}
		if (items.length > MAX_SHOWN)
			console.log(`  … +${items.length - MAX_SHOWN} more`);
	}

	process.exit(1);
}

console.log(`✓ format${mode === "check" ? "-check" : ""}`);
