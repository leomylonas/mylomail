import { copyFileSync } from "node:fs";

// electron-vite's isolated-entry reporter assumes an interactive TTY. CI and the repository
// wrappers intentionally capture output, so provide the two terminal operations as no-ops while
// retaining the standalone preload bundles required by Electron's sandbox.
// A static import cannot be used: ESM evaluates it before these guards are installed.
if (typeof process.stdout.clearLine !== "function") {
	process.stdout.clearLine = () => true;
}
if (typeof process.stdout.moveCursor !== "function") {
	process.stdout.moveCursor = () => true;
}
if (typeof process.stdout.cursorTo !== "function") {
	process.stdout.cursorTo = () => true;
}

const { build } = await import("electron-vite");
await build({ configFile: "ElectronVite.config.ts", logLevel: "error" });
copyFileSync(
	"apps/electron-shell/src/MasterPassword/MasterPasswordPrompt.html",
	"apps/electron-shell/dist/MasterPasswordPrompt.html",
);
