import { expect, test, type ElectronApplication } from "@playwright/test";
import { launchAttachedApp } from "@mylomail/renderer-e2e/AppFixture";

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

async function terminateElectron(app: ElectronApplication): Promise<void> {
	const child = app.process();
	const exited = new Promise<void>((resolve) => {
		child.once("exit", () => resolve());
	});
	await app.evaluate(({ app }) => {
		app.removeAllListeners("before-quit");
		app.quit();
	});
	const graceful = await Promise.race([
		exited.then(() => true),
		new Promise<false>((resolve) => setTimeout(() => resolve(false), 3_000)),
	]);
	if (!graceful) {
		child.kill("SIGKILL");
		await exited;
	}
}
