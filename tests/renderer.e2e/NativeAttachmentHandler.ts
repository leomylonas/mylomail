import { spawnSync } from "node:child_process";
import { writeFileSync } from "node:fs";
import { join } from "node:path";

export interface NativeAttachmentHandler {
	extension: string;
	observesRead: boolean;
	cleanup(): void;
}

export function registerNativeAttachmentHandler(
	root: string,
	marker: string,
	token: string,
): NativeAttachmentHandler {
	const extension = `.mylomail-e2e-${token}`;
	if (process.platform === "win32") {
		return registerWindowsHandler(root, marker, token, extension);
	}
	if (process.platform === "darwin") {
		// LaunchServices intentionally resists changing a user's default application in
		// modern macOS. A standard text document still exercises Electron's native
		// openPath boundary without mutating or depending on that protected preference.
		return { extension: ".txt", observesRead: false, cleanup: () => undefined };
	}
	throw new Error(
		`Native attachment handler is not supported on ${process.platform}.`,
	);
}

function registerWindowsHandler(
	root: string,
	marker: string,
	token: string,
	extension: string,
): NativeAttachmentHandler {
	const handler = join(root, "AttachmentHandler.cjs");
	writeFileSync(
		handler,
		`const fs = require("node:fs");
const path = process.argv[2];
const content = fs.readFileSync(path).toString("base64");
fs.writeFileSync(${JSON.stringify(marker)}, JSON.stringify({ path, content }));
`,
	);
	const extensionKey = `HKCU\\Software\\Classes\\${extension}`;
	const programId = `MyloMail.E2E.${token}`;
	const programKey = `HKCU\\Software\\Classes\\${programId}`;
	run("reg.exe", ["ADD", extensionKey, "/ve", "/d", programId, "/f"]);
	run("reg.exe", [
		"ADD",
		`${programKey}\\shell\\open\\command`,
		"/ve",
		"/d",
		`"${process.execPath}" "${handler}" "%1"`,
		"/f",
	]);

	return {
		observesRead: true,
		extension,
		cleanup: () => {
			spawnSync("reg.exe", ["DELETE", extensionKey, "/f"]);
			spawnSync("reg.exe", ["DELETE", programKey, "/f"]);
		},
	};
}

function run(command: string, args: readonly string[]): void {
	const result = spawnSync(command, args, { encoding: "utf8" });
	if (result.error) throw result.error;
	if (result.status !== 0) {
		throw new Error(
			`${command} ${args.join(" ")} exited ${result.status}: ${result.stderr}`,
		);
	}
}
