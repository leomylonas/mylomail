import { expect, test, type ElectronApplication } from "@playwright/test";
import { launchAttachedApp } from "@mylomail/renderer-e2e/AppFixture";
import { createImapAccount } from "@mylomail/renderer-e2e/SeedAccount";

interface WindowBounds {
	x: number;
	y: number;
	width: number;
	height: number;
}

test("window bounds restore and new windows inherit an offset", async () => {
	const launched = await launchAttachedApp();
	let firstClosed = false;
	let second: Awaited<ReturnType<typeof launched.restartElectron>> | undefined;

	try {
		await launched.window.waitForTimeout(1_000);
		const saved = await launched.app.evaluate(({ BrowserWindow }) => {
			const [window] = BrowserWindow.getAllWindows();
			if (!window) throw new Error("No Electron window exists.");
			window.setBounds({ x: 80, y: 90, width: 880, height: 620 });
			return window.getBounds();
		});
		await launched.window.evaluate(() => window.windows?.open());
		await expect
			.poll(() =>
				launched.app.evaluate(
					({ BrowserWindow }) => BrowserWindow.getAllWindows().length,
				),
			)
			.toBe(2);
		const bounds = await launched.app.evaluate(({ BrowserWindow }) =>
			BrowserWindow.getAllWindows().map((window) => window.getBounds()),
		);
		expect(bounds).toContainEqual({
			x: saved.x + 24,
			y: saved.y + 24,
			width: saved.width,
			height: saved.height,
		});
		await launched.app.evaluate(({ BrowserWindow }, expected) => {
			const primary = BrowserWindow.getAllWindows().find(
				(window) =>
					JSON.stringify(window.getBounds()) === JSON.stringify(expected),
			);
			if (!primary)
				throw new Error("Primary Electron window did not retain its bounds.");
			primary.emit("move");
			primary.emit("resize");
		}, saved);
		await expect
			.poll(
				() =>
					launched.window.evaluate(async () => {
						const response = await fetch("/shell-settings");
						const settings = (await response.json()) as {
							windowBoundsJson?: string;
						};
						return settings.windowBoundsJson
							? (JSON.parse(settings.windowBoundsJson) as WindowBounds)
							: undefined;
					}),
				{ timeout: 10_000 },
			)
			.toEqual(saved);

		await terminateElectron(launched.app);
		firstClosed = true;
		second = await launched.restartElectron();
		await expect
			.poll(() =>
				second!.app.evaluate(({ BrowserWindow }) => {
					const [window] = BrowserWindow.getAllWindows();
					return window?.getBounds();
				}),
			)
			.toEqual(saved);
	} finally {
		if (!firstClosed) await terminateElectron(launched.app);
		if (second) await terminateElectron(second.app);
		await launched.stopBackend();
	}
});

test("the final panel resize survives renderer teardown before the debounce", async () => {
	const launched = await launchAttachedApp();
	let firstClosed = false;
	let second: Awaited<ReturnType<typeof launched.restartElectron>> | undefined;

	try {
		// Without an account the shell shows the full-page first-run form, not the panels.
		await createImapAccount(launched.window, 13143);
		await expect(launched.window.locator("#sidebar")).toBeVisible({
			timeout: 60_000,
		});
		const initialSidebarWidth = await launched.window
			.locator("#sidebar")
			.evaluate((element) => element.getBoundingClientRect().width);
		const separator = launched.window.getByRole("separator").first();
		const bounds = await separator.boundingBox();
		if (!bounds) throw new Error("The first panel separator is not visible.");
		await launched.window.mouse.move(
			bounds.x + bounds.width / 2,
			bounds.y + bounds.height / 2,
		);
		await launched.window.mouse.down();
		await launched.window.mouse.move(
			bounds.x + bounds.width / 2 + 120,
			bounds.y,
			{
				steps: 2,
			},
		);
		await launched.window.mouse.up();
		const resizedSidebarWidth = await launched.window
			.locator("#sidebar")
			.evaluate((element) => element.getBoundingClientRect().width);
		expect(resizedSidebarWidth).toBeGreaterThan(initialSidebarWidth + 40);

		// Quit immediately after pointer release. onLayoutChanged must dispatch that final
		// completed resize without introducing a deferred window where teardown can drop it.
		await terminateElectron(launched.app, false);
		firstClosed = true;
		second = await launched.restartElectron();
		await expect
			.poll(() =>
				second!.window
					.locator("#sidebar")
					.evaluate((element) => element.getBoundingClientRect().width),
			)
			.toBeCloseTo(resizedSidebarWidth, 0);
	} finally {
		if (!firstClosed) await terminateElectron(launched.app);
		if (second) await terminateElectron(second.app);
		await launched.stopBackend();
	}
});

async function terminateElectron(
	app: ElectronApplication,
	bypassQuitBarriers = true,
): Promise<void> {
	const child = app.process();
	const exited = new Promise<void>((resolve) => {
		child.once("exit", () => resolve());
	});
	await app.evaluate(({ app }, bypass) => {
		if (bypass) app.removeAllListeners("before-quit");
		app.quit();
	}, bypassQuitBarriers);
	const graceful = await Promise.race([
		exited.then(() => true),
		new Promise<false>((resolve) => setTimeout(() => resolve(false), 3_000)),
	]);
	if (!graceful) {
		child.kill("SIGKILL");
		await exited;
	}
}
