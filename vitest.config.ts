import { defineConfig } from "vitest/config";
import { fileURLToPath } from "node:url";

const repositoryRoot = fileURLToPath(new URL(".", import.meta.url));

export default defineConfig({
	resolve: {
		alias: {
			"@mylomail/electron-shell": `${repositoryRoot}apps/electron-shell/src`,
		},
	},
	test: {
		passWithNoTests: true,
	},
});
