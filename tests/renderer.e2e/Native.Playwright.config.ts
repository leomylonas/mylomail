import { defineConfig } from "@playwright/test";
import baseConfig from "@mylomail/renderer-e2e/Playwright.config";

/** Runs packaged native-shell checks that need Windows or macOS host integration. */
export default defineConfig({
	...baseConfig,
	testMatch: "**/NativeAttachmentOpen.e2e.ts",
	workers: 1,
});
