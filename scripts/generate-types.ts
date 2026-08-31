/**
 * Generates the TypeScript half of the contract: SignalR hub proxies via
 * TypedSignalR.Client.TypeScript, and DTO interfaces plus Zod schemas via TypeContractor.
 *
 * Types under `packages/shared-types/src` are generated. Never hand-edit them.
 */

import { execFileSync } from "node:child_process";
import { existsSync, mkdirSync, rmSync, symlinkSync } from "node:fs";
import { join, resolve } from "node:path";

const root = process.cwd();
const ASSEMBLY = "server/MyloMail.Api/bin/Debug/net8.0/MyloMail.Api.dll";
const DOTNET_MAJOR = 8;

const run = (args: string[]): void => {
	execFileSync("dotnet", args, { cwd: root, stdio: "inherit" });
};

const capture = (args: string[]): string =>
	execFileSync("dotnet", args, { cwd: root, encoding: "utf8" });

/**
 * Locates the reference packs directory (`<dotnet root>/packs`) by asking the SDK where it
 * lives, rather than guessing at an install location that differs across platforms and
 * package managers.
 */
const findPacksPath = (): string => {
	const basePath = capture(["--info"])
		.split("\n")
		.map((line) => /^\s*Base Path:\s*(.+?)\s*$/.exec(line)?.[1])
		.find((match): match is string => match !== undefined);

	if (basePath === undefined) {
		throw new Error(
			"could not determine the .NET SDK base path from `dotnet --info`",
		);
	}

	// Base Path is `<dotnet root>/sdk/<version>/`; the packs sit alongside `sdk`.
	return resolve(basePath, "..", "..", "packs");
};

/**
 * Works around an upstream bug: TypeContractor builds its pack lookup as
 * `$"{packPath}\{packName}"` with a hardcoded backslash, so on any non-Windows platform the
 * directory never exists and it aborts with `FileNotFoundException` before reading anything.
 * Every path below that first join uses `Path.Combine` and is fine.
 *
 * So we hand it a directory containing symlinks whose names embed the literal backslash it
 * expects. Delete this once the tool joins its paths portably.
 *
 * @see https://github.com/PerfectlyNormal/TypeContractor — `ReflectionContextHelper.GetNetCorePack`
 */
const packsPathFor = (packs: string): string => {
	if (process.platform === "win32") {
		return packs;
	}

	const shimRoot = join(root, ".dev", "typecontractor-packs");
	rmSync(shimRoot, { recursive: true, force: true });
	mkdirSync(shimRoot, { recursive: true });

	for (const pack of [
		"Microsoft.NETCore.App.Ref",
		"Microsoft.AspNetCore.App.Ref",
	]) {
		const target = join(packs, pack);
		if (!existsSync(target)) {
			throw new Error(`missing reference pack: ${target}`);
		}
		symlinkSync(target, join(shimRoot, `packs\\${pack}`), "dir");
	}

	return join(shimRoot, "packs");
};

run(["build", "server/MyloMail.Api", "--nologo", "-tl:off", "-clp:ErrorsOnly"]);

run([
	"tsrts",
	"--project",
	"server/MyloMail.Api/MyloMail.Api.csproj",
	"--output",
	"packages/shared-types/src/SignalR",
]);

// tsrts prints its exceptions and exits zero, so a failed transpilation looks exactly like a
// successful one to the caller. It once emitted only the shared components and no hub proxy
// at all, and nothing noticed. Assert on the artefact instead of trusting the exit code.
const hubProxy = join(
	root,
	"packages/shared-types/src/SignalR/TypedSignalR.Client/MyloMail.Api.Hubs.ts",
);
if (!existsSync(hubProxy)) {
	throw new Error(
		"tsrts produced no hub proxy — check its output above for a transpilation exception, " +
			"usually a DTO missing [TranspilationSource].",
	);
}

run([
	"typecontractor",
	"--assembly",
	ASSEMBLY,
	"--output",
	"packages/shared-types/src/Api",
	"--packs-path",
	packsPathFor(findPacksPath()),
	"--dotnet-version",
	String(DOTNET_MAJOR),
	"--build-zod-schemas",
	// PascalCase file names, matching the hand-written frontend convention. Pinned
	// explicitly because the tool's default casing changed between 0.22.1 and 1.0.0, and a
	// default that moves silently renames every generated file on a tool upgrade.
	"--casing",
	"Pascal",
	// Without this every path carries a redundant `MyloMail/Api/` prefix, so an import
	// reads `.../Api/MyloMail/Api/Contracts/HealthDto`.
	"--strip",
	"MyloMail.Api",
]);
