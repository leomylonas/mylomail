import { defineConfig } from "vite";
import { fileURLToPath } from "node:url";

/**
 * Bundles the preload script as CommonJS.
 *
 * A sandboxed preload cannot be an ES module — Electron loads it in a restricted context that
 * has no module loader — and sandboxing is not negotiable here, because the renderer displays
 * remote-authored message HTML.
 */
export default defineConfig({
	build: {
		outDir: fileURLToPath(new URL("./dist", import.meta.url)),
		emptyOutDir: false,
		ssr: true,
		target: "node20",
		rollupOptions: {
			input: {
				Preload: fileURLToPath(new URL("./src/Preload.ts", import.meta.url)),
				MasterPasswordPreload: fileURLToPath(
					new URL(
						"./src/MasterPassword/MasterPasswordPreload.ts",
						import.meta.url,
					),
				),
			},
			external: ["electron"],
			output: { entryFileNames: "[name].cjs", format: "cjs" },
		},
	},
});
