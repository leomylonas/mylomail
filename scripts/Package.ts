import { mkdirSync, rmSync } from "node:fs";
import { arch, platform } from "node:process";
import { spawnSync } from "node:child_process";
import { join } from "node:path";

const runtimeIdentifiers: Partial<
	Record<NodeJS.Platform, Partial<Record<string, string>>>
> = {
	linux: { x64: "linux-x64", arm64: "linux-arm64" },
	win32: { x64: "win-x64", arm64: "win-arm64" },
	darwin: { x64: "osx-x64", arm64: "osx-arm64" },
};

const runtimeIdentifier = runtimeIdentifiers[platform]?.[arch];
if (!runtimeIdentifier) {
	throw new Error(`Packaging is not supported on ${platform}-${arch}.`);
}

const backendOutput = join("dist", "backend");
rmSync(backendOutput, { recursive: true, force: true });
mkdirSync(backendOutput, { recursive: true });

run("pnpm", ["build:electron"]);
run("dotnet", [
	"publish",
	"server/MyloMail.Api/MyloMail.Api.csproj",
	"--nologo",
	"--configuration",
	"Release",
	"--runtime",
	runtimeIdentifier,
	"--self-contained",
	"true",
	"--output",
	backendOutput,
	"-p:PublishSingleFile=false",
	"-p:PublishTrimmed=false",
]);

run("pnpm", [
	"exec",
	"electron-builder",
	"--config",
	"ElectronBuilder.yml",
	"--publish",
	"never",
	...(process.argv.includes("--dir") ? ["--dir"] : []),
]);

function run(command: string, args: readonly string[]): void {
	const result = spawnSync(command, args, {
		stdio: "inherit",
		shell: platform === "win32",
	});
	if (result.error) throw result.error;
	if (result.status !== 0) {
		throw new Error(`${command} ${args.join(" ")} exited ${result.status}.`);
	}
}
