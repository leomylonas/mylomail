import { spawnSync } from "node:child_process";
import { platform } from "node:process";
import { join } from "node:path";

const root = process.cwd();

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
