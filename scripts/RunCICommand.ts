import { spawnSync } from "node:child_process";
import { isWindows } from "./tooling.ts";

const [command, ...args] = process.argv.slice(2);
if (!command) {
	throw new Error("RunCICommand requires a command.");
}

const result = spawnSync(command, args, {
	cwd: process.cwd(),
	encoding: "utf8",
	maxBuffer: 50 * 1024 * 1024,
	shell: isWindows,
});
const output = `${result.stdout ?? ""}${result.stderr ?? ""}`;
process.stdout.write(output);

if (result.error) {
	throw result.error;
}
if (result.status === 0) {
	process.exit(0);
}

const diagnostic = output.trim().split("\n").slice(-40).join("\n");
if (process.env.GITHUB_ACTIONS === "true") {
	const title = escapeWorkflowProperty(`${command} ${args.join(" ")}`);
	console.log(`::error title=${title}::${escapeWorkflowData(diagnostic)}`);
}
process.exit(result.status ?? 1);

function escapeWorkflowData(value: string): string {
	return value
		.replaceAll("%", "%25")
		.replaceAll("\r", "%0D")
		.replaceAll("\n", "%0A");
}

function escapeWorkflowProperty(value: string): string {
	return escapeWorkflowData(value)
		.replaceAll(":", "%3A")
		.replaceAll(",", "%2C");
}
