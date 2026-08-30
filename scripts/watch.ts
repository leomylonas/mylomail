/**
 * Background watcher. Fans tsc, ESLint and dotnet build into ONE current-state file.
 *
 * Run once per session: `pnpm watch`. Readers call `pnpm status`, which is far cheaper
 * than invoking the tools themselves — and, because the compilers stay warm, faster.
 */

import { spawn, type ChildProcess } from "node:child_process";
import { writeFileSync, renameSync, mkdirSync } from "node:fs";
import { dirname } from "node:path";
import {
	type Diagnostic,
	type StatusFile,
	type ToolName,
	type ToolStatus,
	PATTERNS,
	STATUS_PATH,
	dedupe,
	generation,
	isWindows,
	relativePath,
} from "./tooling.ts";

const ROOT = process.cwd();
const OUT = STATUS_PATH(ROOT);
const HEARTBEAT_MS = 5_000;
const WRITE_DEBOUNCE_MS = 150;
const ESLINT_POLL_MS = 4_000;
const RESTART_DELAY_MS = 2_000;

mkdirSync(dirname(OUT), { recursive: true });

interface MutableToolState {
	status: ToolStatus;
	diagnostics: Diagnostic[];
}

const state: Record<ToolName, MutableToolState> = {
	tsc: { status: "pending", diagnostics: [] },
	eslint: { status: "pending", diagnostics: [] },
	dotnet: { status: "pending", diagnostics: [] },
};

let writeTimer: NodeJS.Timeout | undefined;

/** Coalesce bursts — several tools often report within milliseconds of each other. */
function scheduleWrite(): void {
	clearTimeout(writeTimer);
	writeTimer = setTimeout(flush, WRITE_DEBOUNCE_MS);
}

function flush(): void {
	const tools = {} as StatusFile["tools"];
	let ok = true;

	for (const name of Object.keys(state) as ToolName[]) {
		const tool = state[name];
		tools[name] = { status: tool.status, ...dedupe(tool.diagnostics) };
		if (tool.status !== "ok") ok = false;
	}

	const payload: StatusFile = {
		ok,
		generation: generation(ROOT),
		heartbeat: Date.now(),
		tools,
	};

	// Atomic: a reader must never observe a half-written file.
	const tmp = `${OUT}.tmp`;
	writeFileSync(tmp, JSON.stringify(payload, null, 2));
	renameSync(tmp, OUT);
}

type Parser = (line: string) => Diagnostic | null;

interface WatchOptions {
	name: ToolName;
	command: string;
	args: string[];
	parse: Parser;
	/** Matches the line a tool prints when it begins a fresh compile cycle. */
	cycleStart: RegExp;
}

function watch({ name, command, args, parse, cycleStart }: WatchOptions): void {
	const start = (): void => {
		const child: ChildProcess = spawn(command, args, { cwd: ROOT, shell: isWindows });
		let buffer = "";

		const onData = (chunk: Buffer): void => {
			buffer += chunk.toString();
			const lines = buffer.split("\n");
			buffer = lines.pop() ?? "";

			for (const line of lines) {
				if (cycleStart.test(line)) {
					// A new compile started — discard the previous run's diagnostics.
					state[name].diagnostics = [];
					state[name].status = "running";
					scheduleWrite();
					continue;
				}
				const diagnostic = parse(line);
				if (diagnostic) {
					state[name].diagnostics.push(diagnostic);
					state[name].status = "error";
					scheduleWrite();
				}
			}

			if (state[name].status === "running" && state[name].diagnostics.length === 0) {
				state[name].status = "ok";
				scheduleWrite();
			}
		};

		child.stdout?.on("data", onData);
		child.stderr?.on("data", onData);

		child.on("exit", (code: number | null) => {
			// A watcher must not exit silently: doing so would report stale results
			// forever. Mark it unverified and restart.
			console.error(`[watch] ${name} exited (${code}); restarting in ${RESTART_DELAY_MS}ms`);
			state[name].status = "pending";
			scheduleWrite();
			setTimeout(start, RESTART_DELAY_MS);
		});
	};

	start();
}

watch({
	name: "tsc",
	command: "npx",
	args: ["tsc", "--noEmit", "--watch", "--pretty", "false", "--preserveWatchOutput"],
	cycleStart: /File change detected|Starting compilation/,
	parse: (line) => {
		const m = PATTERNS.tsc.exec(line);
		if (!m) return null;
		return { file: relativePath(ROOT, m[1]), line: Number(m[2]), code: m[4], message: m[5] };
	},
});

watch({
	name: "dotnet",
	command: "dotnet",
	args: ["watch", "build", "--project", "server/MyloMail.Api", "--nologo", "-tl:off"],
	cycleStart: /Started|Building|File changed/,
	parse: (line) => {
		const m = PATTERNS.dotnet.exec(line);
		if (!m) return null;
		return { file: relativePath(ROOT, m[1]), line: Number(m[2]), code: m[4], message: m[5] };
	},
});

// ESLint has no watch mode; poll instead.
setInterval(() => {
	const child = spawn("npx", ["eslint", ".", "--quiet", "-f", "unix"], {
		cwd: ROOT,
		shell: isWindows,
	});
	const diagnostics: Diagnostic[] = [];

	child.stdout?.on("data", (chunk: Buffer) => {
		for (const line of chunk.toString().split("\n")) {
			const m = PATTERNS.eslint.exec(line);
			if (m) {
				diagnostics.push({
					file: relativePath(ROOT, m[1]),
					line: Number(m[2]),
					code: m[5],
					message: m[4],
				});
			}
		}
	});

	child.on("exit", () => {
		state.eslint.diagnostics = diagnostics;
		state.eslint.status = diagnostics.length > 0 ? "error" : "ok";
		scheduleWrite();
	});
}, ESLINT_POLL_MS);

// Liveness: a dead watcher must be detectable by its readers.
setInterval(flush, HEARTBEAT_MS);
flush();

console.log(`[watch] running; status → ${relativePath(ROOT, OUT)}`);
