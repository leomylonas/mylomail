import { spawnSync } from "node:child_process";
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
run("pnpm", ["build:electron"]);
if (process.env.MYLOMAIL_BACKEND_ARGS === undefined)
	run("dotnet", ["build", "server/MyloMail.Api"]);
run(
	"pnpm",
	[
		"exec",
		"electron",
		...sandboxArguments(),
		"apps/electron-shell/dist/Main.js",
	],
	{
		MYLOMAIL_RENDERER_PATH: join(root, "apps", "renderer", "dist"),
		MYLOMAIL_BACKEND_ARGS: backendArguments,
	},
);

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
