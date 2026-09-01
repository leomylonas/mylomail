import { app, BrowserWindow, Menu, nativeImage, Tray } from "electron";

/**
 * A tiny 32x32 envelope glyph, embedded rather than loaded from disk (§13 Epic 10). The main
 * process bundle is a single rollup file with no static-asset copy step, so a file on disk
 * would need its own build wiring just for one icon; a data URL needs none.
 */
const iconDataUrl =
	"data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAACAAAAAgCAYAAABzenr0AAAAqElEQVR4nO2WSw6AIAwF+4wH8I6eyztyA1w1QaxYStWFfSu+meETAlEk8sfknDOXITW+FQCYpY5l9XdJGw51ACAimjSDPeH14g4CZWfa4CLSgp8EeFAt8hRcFJAm9ErUu9e6U5cCVokafHehmwK9EtpVdwloJSxwtYAkwcCe85YiPkR3EiXcCuaod0ASGYWbBUro6LNtFvCADwt4JARC4LMvGf+IIpHIDqB7VA9Nh/DzAAAAAElFTkSuQmCC";

let tray: Tray | undefined;

/**
 * Creates the tray icon once, the first time close-behaviour actually needs it. Left `undefined`
 * for the ordinary `QuitApp` session so nothing extra appears in the system tray for the common
 * case.
 *
 * The menu always offers an explicit "Quit" (§8, §13 Epic 10) — minimising to tray must never
 * leave the app with no visible way out.
 */
export function ensureTray(): Tray {
	if (tray) return tray;

	tray = new Tray(nativeImage.createFromDataURL(iconDataUrl));
	tray.setToolTip("MyloMail");
	tray.setContextMenu(
		Menu.buildFromTemplate([
			{
				label: "Open MyloMail",
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
