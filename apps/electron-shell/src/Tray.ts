import { app, BrowserWindow, Menu, Tray } from "electron";
import { getTrayIcon } from "@mylomail/electron-shell/AppIcon";

let tray: Tray | undefined;

/**
 * Creates the tray icon once whenever minimise-to-tray is enabled. Keeping it visible for
 * the whole session makes the background-running state discoverable before the first close;
 * an ordinary `QuitApp` session still creates no tray object.
 *
 * The menu always offers an explicit "Quit" (§8, §13 Epic 10) — minimising to tray must never
 * leave the app with no visible way out.
 */
export function ensureTray(): Tray {
	if (tray) return tray;

	tray = new Tray(getTrayIcon());
	tray.setToolTip("Mylo Mail");
	tray.setContextMenu(
		Menu.buildFromTemplate([
			{
				label: "Open Mylo Mail",
				click: () => showAllWindows(),
			},
			{ type: "separator" },
			{ label: "Quit", click: () => app.quit() },
		]),
	);
	tray.on("click", () => showAllWindows());
	return tray;
}

export function destroyTray(): void {
	tray?.destroy();
	tray = undefined;
}

function showAllWindows(): void {
	for (const window of BrowserWindow.getAllWindows()) {
		if (window.isMinimized()) window.restore();
		window.show();
		window.focus();
	}
}
