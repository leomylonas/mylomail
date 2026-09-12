import { expect, test } from "@playwright/test";
import { launchPackagedApp } from "@mylomail/renderer-e2e/AppFixture";

test("the packaged shell starts its bundled self-contained backend and renderer", async () => {
	const { app, window } = await launchPackagedApp();
	try {
		await expect(
			window.getByRole("heading", { name: "Add account" }),
		).toBeVisible({ timeout: 30_000 });
		expect(window.url()).toMatch(/^http:\/\/127\.0\.0\.1:\d+\//u);
		const fonts = await window.evaluate(async () => {
			const requests = [
				'300 16px "IBM Plex Sans"',
				'400 16px "IBM Plex Sans"',
				'italic 400 16px "IBM Plex Sans"',
				'600 16px "IBM Plex Sans"',
				'400 16px "IBM Plex Mono"',
			];
			const loaded = await Promise.all(
				requests.map(
					async (request) =>
						(await document.fonts.load(request, "MyloMail")).length,
				),
			);
			await document.fonts.ready;
			const assets = performance
				.getEntriesByType("resource")
				.map((entry) => entry.name)
				.filter((name) => /\/assets\/IBMPlex.+\.woff2$/u.test(name));
			return { assets, loaded };
		});
		expect(fonts.loaded).toEqual([1, 1, 1, 1, 1]);
		expect(new Set(fonts.assets).size).toBe(5);
	} finally {
		await app.close();
	}
});
