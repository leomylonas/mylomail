import { expect, test } from "@playwright/test";
import { launchPackagedApp } from "@mylomail/renderer-e2e/AppFixture";

test("the packaged shell starts its bundled self-contained backend and renderer", async () => {
	const { app, window } = await launchPackagedApp();
	try {
		await expect(
			window.getByRole("heading", { name: "Add account" }),
		).toBeVisible({ timeout: 30_000 });
		expect(window.url()).toMatch(/^http:\/\/127\.0\.0\.1:\d+\//u);
	} finally {
		await app.close();
	}
});
