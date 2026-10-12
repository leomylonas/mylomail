import { spawn, spawnSync } from "node:child_process";
import { existsSync, statSync } from "node:fs";
import { createRequire } from "node:module";
import { loadEnvFile, platform } from "node:process";
import { dirname, join } from "node:path";

const root = process.cwd();

// A development checkout keeps the application's own provider client registration in
// .dev/provider-test.env (gitignored). Only the registration is passed on, never the test
// accounts' passwords or token caches that share the file, and anything already in the
// environment wins.
const providerEnvironment = join(root, ".dev", "provider-test.env");
const registrationNames = [
	"GMAIL_CLIENT_ID",
	"GMAIL_CLIENT_SECRET",
	"GRAPH_CLIENT_ID",
];
if (existsSync(providerEnvironment)) {
	const before = { ...process.env };
	loadEnvFile(providerEnvironment);
	for (const name of Object.keys(process.env))
		if (!(name in before) && !registrationNames.includes(name))
			delete process.env[name];
}

// The shell gives the backend 30 seconds to announce its port. `dotnet run` would compile
// first, and a cold compile alone can exceed that, so build up front (incremental and
// quick when warm) and start the backend with --no-build. An explicit
// MYLOMAIL_BACKEND_ARGS is the caller's own launch and is left untouched.
const backendArguments =
	process.env.MYLOMAIL_BACKEND_ARGS ??
	"run --no-build --project server/MyloMail.Api";
// `pnpm app` is the development environment: every start is a fresh build running fresh code, so
// anything left over from an earlier launch — an app window, or a backend that outlived it —
// goes first. The patterns are specific to this checkout's backend and shell entry point.
const staleProcessPatterns = [
	join(root, "server", "MyloMail.Api", "bin"),
	"run --no-build --project server/MyloMail.Api",
	"apps/electron-shell/dist/Main.js",
];
killStaleProcesses();
run("pnpm", ["build:electron"]);
if (process.env.MYLOMAIL_BACKEND_ARGS === undefined)
	run("dotnet", ["build", "server/MyloMail.Api"]);
runApp();

/**
 * Runs the app in its own process group and takes the whole group down with the launcher, so
 * stopping `pnpm app` — Ctrl-C, a signal, or the shell closing — never leaves the backend (a
 * grandchild of the shell) running with nobody to talk to.
 */
function runApp(): void {
	const child = spawn(
		"pnpm",
		[
			"exec",
			"electron",
			...sandboxArguments(),
			"apps/electron-shell/dist/Main.js",
		],
		{
			cwd: root,
			env: {
				...process.env,
				MYLOMAIL_RENDERER_PATH: join(root, "apps", "renderer", "dist"),
				MYLOMAIL_BACKEND_ARGS: backendArguments,
			},
			stdio: "inherit",
			shell: platform === "win32",
			detached: platform !== "win32",
		},
	);

	let stopping = false;
	const stopGroup = (signal: NodeJS.Signals = "SIGTERM") => {
		if (child.pid === undefined) return;
		try {
			if (platform === "win32") child.kill(signal);
			else process.kill(-child.pid, signal);
		} catch {
			// The group is already gone.
		}
	};
	for (const signal of ["SIGINT", "SIGTERM", "SIGHUP"] as const) {
		process.on(signal, () => {
			if (stopping) return;
			stopping = true;
			stopGroup(signal);
			// A shell that ignores the signal must not leave the launcher hanging.
			setTimeout(() => {
				stopGroup("SIGKILL");
				killStaleProcesses();
				process.exit(130);
			}, 5000).unref();
		});
	}
	child.on("error", (error) => {
		console.error(error);
		process.exitCode = 1;
	});
	child.on("exit", (code) => {
		// Whatever the shell left behind, including a backend it did not get to stop.
		stopGroup("SIGKILL");
		killStaleProcesses();
		process.exitCode = code ?? process.exitCode ?? 1;
	});
}

function killStaleProcesses(): void {
	if (platform === "win32") return;
	for (const signal of ["-TERM", "-KILL"]) {
		for (const pattern of staleProcessPatterns)
			spawnSync("pkill", [signal, "-f", pattern], { stdio: "ignore" });
		if (signal === "-TERM") spawnSync("sleep", ["1"]);
	}
}

function run(
	command: string,
	args: readonly string[],
	environment: NodeJS.ProcessEnv = {},
): void {
	const result = spawnSync(command, args, {
		cwd: root,
		env: { ...process.env, ...environment },
		stdio: "inherit",
		shell: platform === "win32",
	});
	if (result.error) throw result.error;
	process.exitCode = result.status ?? 1;
	if (process.exitCode !== 0) process.exit();
}

// On Linux Electron's SUID helper must be root-owned with mode 4755, which a pnpm install
// never produces, and Ubuntu 24.04+ also blocks the unprivileged user-namespace fallback.
// Either way Chromium aborts at startup. For this development launcher only, run without
// the sandbox when the helper is not usable; a correctly configured helper keeps it.
function sandboxArguments(): string[] {
	if (platform !== "linux") return [];
	const binary = createRequire(join(root, "package.json"))(
		"electron",
	) as string;
	const helper = join(dirname(binary), "chrome-sandbox");
	if (!existsSync(helper)) return [];
	const stats = statSync(helper);
	const usable = stats.uid === 0 && (stats.mode & 0o4000) !== 0;
	if (usable) return [];
	console.warn(
		`chrome-sandbox is not root-owned with mode 4755 (${helper}); launching with --no-sandbox`,
	);
	return ["--no-sandbox"];
}
