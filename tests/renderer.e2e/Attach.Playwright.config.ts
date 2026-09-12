import { defineConfig } from "@playwright/test";
import baseConfig from "@mylomail/renderer-e2e/Playwright.config";

/** Runs only the externally owned backend workflow for fast debugger/CI attach verification. */
export default defineConfig({
	...baseConfig,
	testMatch: "**/AttachMode.e2e.ts",
});
