import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";
import { fileURLToPath } from "node:url";

/**
 * Builds the renderer for Electron to load from a file.
 *
 * `base: "./"` because the window loads `index.html` from disk rather than from a server, so
 * absolute asset paths would resolve against the filesystem root.
 */
export default defineConfig({
	// Set explicitly because the config is passed by path from the workspace root, so Vite's
	// default of "the current directory" would look for index.html in the wrong place.
	root: fileURLToPath(new URL(".", import.meta.url)),
	base: "./",
	plugins: [react()],
	resolve: {
		alias: {
			"@mylomail/renderer": fileURLToPath(new URL("./src", import.meta.url)),
		},
	},
	build: { outDir: "dist", emptyOutDir: true },
});
