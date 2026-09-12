import { spawnSync } from "node:child_process";
import { chmodSync, mkdirSync, writeFileSync } from "node:fs";
import { join } from "node:path";

export interface NativeAttachmentHandler {
	extension: string;
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
		return registerMacHandler(root, marker, token, extension);
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
		extension,
		cleanup: () => {
			spawnSync("reg.exe", ["DELETE", extensionKey, "/f"]);
			spawnSync("reg.exe", ["DELETE", programKey, "/f"]);
		},
	};
}

function registerMacHandler(
	root: string,
	marker: string,
	token: string,
	extension: string,
): NativeAttachmentHandler {
	const bundle = join(root, "MyloMailE2EOpener.app");
	const contents = join(bundle, "Contents");
	const macos = join(contents, "MacOS");
	const executable = join(macos, "MyloMailE2EOpener");
	const source = join(root, "AttachmentHandler.swift");
	mkdirSync(macos, { recursive: true });
	writeFileSync(
		source,
		`import AppKit

final class AppDelegate: NSObject, NSApplicationDelegate {
	func application(_ sender: NSApplication, openFiles filenames: [String]) {
		guard let path = filenames.first else {
			sender.reply(toOpenOrPrint: .failure)
			sender.terminate(nil)
			return
		}
		do {
			let proof: [String: String] = [
				"path": path,
				"content": try Data(contentsOf: URL(fileURLWithPath: path)).base64EncodedString(),
			]
			let encoded = try JSONSerialization.data(withJSONObject: proof)
			try encoded.write(to: URL(fileURLWithPath: ${swiftString(marker)}), options: .atomic)
			sender.reply(toOpenOrPrint: .success)
		} catch {
			sender.reply(toOpenOrPrint: .failure)
		}
		sender.terminate(nil)
	}
}

let application = NSApplication.shared
let delegate = AppDelegate()
application.delegate = delegate
application.setActivationPolicy(.prohibited)
application.run()
`,
	);
	run("swiftc", [source, "-framework", "AppKit", "-o", executable]);
	chmodSync(executable, 0o700);
	writeFileSync(
		join(contents, "Info.plist"),
		`<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
	<key>CFBundleDisplayName</key><string>MyloMail E2E Opener</string>
	<key>CFBundleExecutable</key><string>MyloMailE2EOpener</string>
	<key>CFBundleIdentifier</key><string>com.mylomail.e2e.${token}</string>
	<key>CFBundlePackageType</key><string>APPL</string>
	<key>CFBundleDocumentTypes</key>
	<array><dict>
		<key>CFBundleTypeExtensions</key><array><string>${extension.slice(1)}</string></array>
		<key>CFBundleTypeName</key><string>MyloMail E2E attachment</string>
		<key>CFBundleTypeRole</key><string>Viewer</string>
		<key>LSHandlerRank</key><string>Owner</string>
	</dict></array>
	<key>LSBackgroundOnly</key><true/>
</dict>
</plist>
`,
	);
	const launchServices =
		"/System/Library/Frameworks/CoreServices.framework/Frameworks/LaunchServices.framework/Support/lsregister";
	run(launchServices, ["-f", bundle]);

	return {
		extension,
		cleanup: () => {
			spawnSync(launchServices, ["-u", bundle]);
		},
	};
}

function swiftString(value: string): string {
	return `"${value.replaceAll("\\", "\\\\").replaceAll('"', '\\"')}"`;
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
