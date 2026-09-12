import { defineConfig } from "@playwright/test";
import baseConfig from "@mylomail/renderer-e2e/Playwright.config";

/** Runs only the unpacked electron-builder artifact smoke. */
export default defineConfig({
	...baseConfig,
	testMatch: "**/PackagedMode.e2e.ts",
	testIgnore: [],
});
