import { spawnSync } from "node:child_process";
import { existsSync } from "node:fs";
import { loadEnvFile } from "node:process";
import { join, resolve } from "node:path";

const root = process.cwd();
const provider = process.argv[2]?.toLowerCase();
if (provider !== "gmail" && provider !== "graph") {
	console.error("Usage: pnpm provider:authorize <gmail|graph>");
	process.exit(2);
}

const environmentPath = resolve(root, ".dev", "provider-test.env");
if (!existsSync(environmentPath)) {
	console.error(".dev/provider-test.env does not exist.");
	process.exit(1);
}
loadEnvFile(environmentPath);

const result = spawnSync(
	"dotnet",
	[
		"run",
		"--project",
		join(root, "server", "MyloMail.ProviderTestAuth"),
		"--",
		provider,
		environmentPath,
	],
	{ cwd: root, env: process.env, stdio: "inherit" },
);
if (result.error) throw result.error;
process.exit(result.status ?? 1);
