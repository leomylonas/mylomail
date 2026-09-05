import { basename } from "node:path";

// Extensions capable of running code on the user's machine when opened via the OS's default
// handler (`shell.openPath`). This is a warn-before-open list, not a block list — the user can
// always choose "Open" anyway — so a false positive costs one extra click, while a false
// negative silently skips the warning for something that can actually execute. Kept deliberately
// broader than just Windows-native binaries: a message attachment is remote-authored, and a
// recipient's OS/file-association setup is not something this app can assume.
const dangerousExtensions = new Set([
	".application",
	".bat",
	".cmd",
	".com",
	".cpl",
	".desktop",
	".exe",
	".gadget",
	".hta",
	".jar",
	".js",
	".jse",
	".lnk",
	".msc",
	".msi",
	".msp",
	".pif",
	".ps1",
	".reg",
	".scr",
	".sh",
	".vb",
	".vbe",
	".vbs",
	".wsf",
	".wsh",
]);

export function isDangerousAttachment(path: string): boolean {
	const name = basename(path);
	const extension = name.slice(name.lastIndexOf(".")).toLowerCase();
	return dangerousExtensions.has(extension);
}
