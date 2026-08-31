import { defineConfig } from "vitest/config";
import { fileURLToPath } from "node:url";

const repositoryRoot = fileURLToPath(new URL(".", import.meta.url));

export default defineConfig({
	resolve: {
		// Mirrors tsconfig's paths. Kept in step by hand because Vitest does not read them,
		// and a missing entry fails as "cannot find package" rather than as a path problem.
		alias: {
			"@mylomail/electron-shell": `${repositoryRoot}apps/electron-shell/src`,
			"@mylomail/renderer": `${repositoryRoot}apps/renderer/src`,
			"@mylomail/shared-types": `${repositoryRoot}packages/shared-types/src`,
			"@mylomail/ui": `${repositoryRoot}packages/ui/src`,
		},
	},
	test: {
		passWithNoTests: true,
	},
});
