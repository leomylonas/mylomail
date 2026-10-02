import { spawnSync } from "node:child_process";
import { existsSync } from "node:fs";
import { loadEnvFile, platform } from "node:process";
import { join } from "node:path";

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
	"GRAPH_TENANT_ID",
];
if (existsSync(providerEnvironment)) {
	const before = { ...process.env };
	loadEnvFile(providerEnvironment);
	for (const name of Object.keys(process.env))
		if (!(name in before) && !registrationNames.includes(name))
			delete process.env[name];
}

run("pnpm", ["build:electron"]);
run("pnpm", ["exec", "electron", "apps/electron-shell/dist/Main.js"], {
	MYLOMAIL_RENDERER_PATH: join(root, "apps", "renderer", "dist"),
	MYLOMAIL_BACKEND_ARGS:
		process.env.MYLOMAIL_BACKEND_ARGS ?? "run --project server/MyloMail.Api",
});

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
