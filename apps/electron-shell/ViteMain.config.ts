import { defineConfig } from "vite";
import { fileURLToPath } from "node:url";

/** Bundles the Electron main process. Path aliases must be resolved here — Node will not. */
export default defineConfig({
	resolve: {
		alias: {
			"@mylomail/electron-shell": fileURLToPath(
				new URL("./src", import.meta.url),
			),
		},
	},
	build: {
		outDir: fileURLToPath(new URL("./dist", import.meta.url)),
		emptyOutDir: true,
		ssr: true,
		target: "node20",
		rollupOptions: {
			input: fileURLToPath(new URL("./src/Main.ts", import.meta.url)),
			external: ["electron"],
			output: { entryFileNames: "Main.js", format: "es" },
		},
	},
});
