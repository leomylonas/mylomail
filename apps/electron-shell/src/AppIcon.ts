import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { nativeImage } from "electron";
import type { NativeImage } from "electron";

const brandDirectory = join(dirname(fileURLToPath(import.meta.url)), "Brand");
let icon: NativeImage | undefined;
let trayIcon: NativeImage | undefined;

/** The same bundled artwork is available before the backend starts and in packaged builds. */
export function getAppIcon(): NativeImage {
	icon ??= nativeImage.createFromPath(join(brandDirectory, "Icon.png"));
	return icon;
}

export function getTrayIcon(): NativeImage {
	if (!trayIcon) {
		if (process.platform === "darwin") {
			trayIcon = nativeImage
				.createFromPath(join(brandDirectory, "TrayTemplate.png"))
				.resize({ width: 18, height: 18 });
			trayIcon.setTemplateImage(true);
		} else {
			trayIcon = getAppIcon().resize({ width: 32, height: 32 });
		}
	}
	return trayIcon;
}
