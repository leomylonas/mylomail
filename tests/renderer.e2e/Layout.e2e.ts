import { expect, test } from "@playwright/test";
import { launchApp } from "@mylomail/renderer-e2e/AppFixture";
import { createImapAccount } from "@mylomail/renderer-e2e/SeedAccount";

const imapPort = 13143;

test("the main shell remains usable at its minimum window size", async () => {
	const { app, window } = await launchApp();

	try {
		await createImapAccount(window, imapPort);
		await expect(window.getByRole("button", { name: "Contacts" })).toBeVisible({
			timeout: 60_000,
		});

		const size = await app.evaluate(({ BrowserWindow }) => {
			const shell = BrowserWindow.getAllWindows().find((candidate) =>
				candidate.webContents.getURL().startsWith("http://127.0.0.1:"),
			);
			if (!shell) throw new Error("The main shell window is missing.");
			shell.setSize(320, 240);
			return shell.getSize();
		});
		expect(size[0]).toBeGreaterThanOrEqual(720);
		expect(size[1]).toBeGreaterThanOrEqual(480);

		await expect(
			window.getByRole("button", { name: "Toggle reading pane" }),
		).toBeVisible();
		const firstButton = await window
			.getByRole("button", { name: "New message" })
			.boundingBox();
		const lastButton = await window
			.getByRole("button", { name: "Toggle reading pane" })
			.boundingBox();
		expect(firstButton).not.toBeNull();
		expect(lastButton).not.toBeNull();
		expect(lastButton!.y).toBeGreaterThan(firstButton!.y);

		const dimensions = await window.evaluate(() => ({
			viewportWidth: document.documentElement.clientWidth,
			documentWidth: document.documentElement.scrollWidth,
			sidebarWidth: document.getElementById("sidebar")?.getBoundingClientRect()
				.width,
			listWidth: document.getElementById("list")?.getBoundingClientRect().width,
			detailWidth: document.getElementById("detail")?.getBoundingClientRect()
				.width,
		}));
		expect(dimensions.documentWidth).toBeLessThanOrEqual(
			dimensions.viewportWidth,
		);
		expect(dimensions.sidebarWidth).toBeGreaterThan(100);
		expect(dimensions.listWidth).toBeGreaterThan(100);
		expect(dimensions.detailWidth).toBeGreaterThan(100);
	} finally {
		await app.close();
	}
});
