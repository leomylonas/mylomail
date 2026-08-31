import { fileURLToPath } from "node:url";
import { dirname, join } from "node:path";
import { BrowserWindow, ipcMain } from "electron";
import {
	masterPasswordCancelChannel,
	masterPasswordSubmitChannel,
} from "@mylomail/electron-shell/MasterPassword/MasterPasswordChannels";

const here = dirname(fileURLToPath(import.meta.url));

/** Whether the user is choosing a password for the first time or unlocking an existing one. */
export type MasterPasswordMode = "setup" | "unlock";

/**
 * Asks the user for the master password.
 *
 * Resolves with undefined when they decline, which `startBackend` turns into a refusal to
 * start — the honest outcome, since without the password the stored credentials cannot be
 * read and there is nothing to show.
 */
export async function promptForMasterPassword(
	mode: MasterPasswordMode = "setup",
): Promise<string | undefined> {
	// Non-interactive launches — CI, scripted runs, an agent driving the app — supply it
	// directly. Checked before any window exists so a headless run never blocks on one.
	const supplied = process.env.MYLOMAIL_MASTER_PASSWORD;
	if (supplied) return supplied;

	const window = new BrowserWindow({
		width: 460,
		height: 320,
		resizable: false,
		title: "MyloMail",
		webPreferences: {
			preload: join(here, "MasterPasswordPreload.cjs"),
			contextIsolation: true,
			nodeIntegration: false,
			sandbox: true,
		},
	});

	try {
		return await new Promise<string | undefined>((resolve) => {
			let answered = false;
			const settle = (password: string | undefined) => {
				if (answered) return;
				answered = true;
				resolve(password);
			};

			ipcMain.once(masterPasswordSubmitChannel, (_event, password: string) =>
				settle(password),
			);
			ipcMain.once(masterPasswordCancelChannel, () => settle(undefined));

			// Closing the window is declining. Without this the promise would never settle and
			// startup would hang on a window that is no longer there.
			window.once("closed", () => settle(undefined));

			void window.loadFile(
				join(here, "MasterPasswordPrompt.html"),
				mode === "unlock" ? { search: "unlock" } : {},
			);
		});
	} finally {
		// The listeners are `once`, but only one of them fires; the other would outlive this
		// prompt and capture the next one's answer.
		ipcMain.removeAllListeners(masterPasswordSubmitChannel);
		ipcMain.removeAllListeners(masterPasswordCancelChannel);
		if (!window.isDestroyed()) window.destroy();
	}
}
