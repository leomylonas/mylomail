import { fileURLToPath } from "node:url";
import react from "@vitejs/plugin-react";
import { defineConfig } from "electron-vite";

const shell = fileURLToPath(new URL("./apps/electron-shell", import.meta.url));
const renderer = fileURLToPath(new URL("./apps/renderer", import.meta.url));

/** One Electron-aware build graph for the main, sandboxed preload, and renderer targets. */
export default defineConfig({
	main: {
		resolve: {
			alias: {
				"@mylomail/electron-shell": `${shell}/src`,
			},
		},
		build: {
			outDir: `${shell}/dist`,
			emptyOutDir: true,
			target: "node22",
			rollupOptions: {
				input: `${shell}/src/Main.ts`,
				output: { entryFileNames: "Main.js", format: "es" },
			},
		},
	},
	preload: {
		resolve: {
			alias: {
				"@mylomail/electron-shell": `${shell}/src`,
			},
		},
		build: {
			outDir: `${shell}/dist`,
			emptyOutDir: false,
			target: "node22",
			externalizeDeps: false,
			isolatedEntries: true,
			rollupOptions: {
				input: {
					Preload: `${shell}/src/Preload.ts`,
					MasterPasswordPreload: `${shell}/src/MasterPassword/MasterPasswordPreload.ts`,
				},
				output: { entryFileNames: "[name].cjs", format: "cjs" },
			},
		},
	},
	renderer: {
		root: renderer,
		base: "./",
		plugins: [react()],
		resolve: {
			alias: {
				"@mylomail/renderer": `${renderer}/src`,
				"@mylomail/electron-shell": `${shell}/src`,
				"@mylomail/shared-types": fileURLToPath(
					new URL("./packages/shared-types/src", import.meta.url),
				),
				"@mylomail/ui": fileURLToPath(
					new URL("./packages/ui/src", import.meta.url),
				),
			},
		},
		build: {
			outDir: `${renderer}/dist`,
			emptyOutDir: true,
			rollupOptions: {
				input: `${renderer}/index.html`,
			},
		},
	},
});

