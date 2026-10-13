import { spawn } from "node:child_process";
import { shell } from "electron";

/** How long xdg-open gets to say it could not open the file before we take its silence as success. */
const linuxOpenGraceMs = 3000;

/**
 * Opens a file with the desktop's default application, resolving to an error message or "".
 *
 * On Linux `shell.openPath` waits on xdg-open's output pipes, and the application xdg-open starts
 * inherits them, so for a long-lived viewer the promise may not settle at all and the user sees
 * nothing happen. xdg-open is started detached with no pipes instead: it returns as soon as it has
 * handed the file over, and a failure to find or start a handler still comes back as a message.
 */
export function openWithDefaultApplication(path: string): Promise<string> {
	if (process.platform !== "linux") return shell.openPath(path);

	return new Promise((resolve) => {
		const child = spawn("xdg-open", [path], {
			detached: true,
			stdio: "ignore",
		});
		const settle = (message: string) => {
			clearTimeout(timer);
			resolve(message);
		};
		const timer = setTimeout(() => settle(""), linuxOpenGraceMs);
		child.once("error", (error) =>
			settle(`The file could not be opened: ${error.message}`),
		);
		child.once("exit", (code) =>
			settle(
				code === 0 || code === null
					? ""
					: "No application could open this file. Check the default application for its type.",
			),
		);
		child.unref();
	});
}
