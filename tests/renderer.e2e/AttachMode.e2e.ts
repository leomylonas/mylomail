import { expect, test } from "@playwright/test";
import { launchAttachedApp } from "@mylomail/renderer-e2e/AppFixture";

test("Electron attaches to an independently owned backend without spawning or stopping it", async () => {
	const { app, window, backend, stopBackend } = await launchAttachedApp();
	let appClosed = false;

	try {
		await expect(
			window.getByRole("heading", { name: "Add account" }),
		).toBeVisible({ timeout: 30_000 });
		expect(window.url()).toMatch(/^http:\/\/127\.0\.0\.1:\d+\//u);

		await app.close();
		appClosed = true;
		await new Promise<void>((resolve) => setTimeout(resolve, 250));
		expect(backend.killed).toBe(false);
		expect(backend.exitCode).toBeNull();
	} finally {
		if (!appClosed) await app.close();
		await stopBackend();
	}
});
