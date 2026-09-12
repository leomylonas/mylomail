import { chmodSync, mkdtempSync, readFileSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { expect, test, type ElectronApplication } from "@playwright/test";
import { launchApp } from "@mylomail/renderer-e2e/AppFixture";
import { createImapAccount } from "@mylomail/renderer-e2e/SeedAccount";
import { appendMessage, clearInbox } from "@mylomail/renderer-e2e/SeedImap";

const imapPort = 11143;

interface CrashState {
	title?: string;
	message?: string;
	detail?: string;
	relaunchRequested: boolean;
	exitCode?: number;
}

test("a backend crash offers restart while cached mail remains usable", async () => {
	await clearInbox(imapPort);
	await appendMessage(imapPort, "Cached after crash");

	const controlDirectory = mkdtempSync(join(tmpdir(), "mylomail-crash-e2e-"));
	const pidFile = join(controlDirectory, "backend.pid");
	const launcher = join(controlDirectory, "backend-launcher");
	writeFileSync(
		launcher,
		`#!/bin/sh\nprintf '%s' "$$" > ${JSON.stringify(pidFile)}\nexec dotnet "$@"\n`,
	);
	chmodSync(launcher, 0o700);

	const first = await launchApp([], { MYLOMAIL_BACKEND_COMMAND: launcher });
	let firstClosed = false;
	let second: Awaited<ReturnType<typeof launchApp>> | undefined;

	try {
		await createImapAccount(first.window, imapPort);
		const inbox = first.window.getByRole("button", { name: /INBOX/ });
		await expect(inbox).toBeVisible({ timeout: 60_000 });
		await inbox.click();
		const cachedMessage = first.window.getByRole("button", {
			name: /Cached after crash/,
		});
		await expect(cachedMessage).toBeVisible({ timeout: 60_000 });

		await first.app.evaluate(({ app, dialog }) => {
			const state: CrashState = { relaunchRequested: false };
			(
				globalThis as typeof globalThis & { myloMailCrashState?: CrashState }
			).myloMailCrashState = state;
			dialog.showMessageBox = (async (...args: unknown[]) => {
				const options = args.at(-1) as {
					title?: string;
					message?: string;
					detail?: string;
				};
				state.title = options.title;
				state.message = options.message;
				state.detail = options.detail;
				return { response: 0, checkboxChecked: false };
			}) as typeof dialog.showMessageBox;
			app.relaunch = () => {
				state.relaunchRequested = true;
			};
			app.exit = (code) => {
				state.exitCode = code;
			};
		});

		await expect
			.poll(() => {
				try {
					return Number.parseInt(readFileSync(pidFile, "utf8"), 10);
				} catch {
					return undefined;
				}
			})
			.toBeGreaterThan(1);
		const backendPid = Number.parseInt(readFileSync(pidFile, "utf8"), 10);
		process.kill(backendPid, "SIGKILL");

		await expect
			.poll(() =>
				first.app.evaluate(
					() =>
						(
							globalThis as typeof globalThis & {
								myloMailCrashState?: CrashState;
							}
						).myloMailCrashState,
				),
			)
			.toMatchObject({
				title: "MyloMail stopped unexpectedly",
				message: "MyloMail's background process stopped unexpectedly.",
				relaunchRequested: true,
				exitCode: 0,
			});
		await expect(cachedMessage).toBeVisible();

		await terminateElectron(first.app);
		firstClosed = true;
		second = await launchApp([], {}, first.dataDirectory);
		const restoredInbox = second.window.getByRole("button", { name: /INBOX/ });
		await expect(restoredInbox).toBeVisible({ timeout: 10_000 });
		await restoredInbox.click();
		await expect(
			second.window.getByRole("button", { name: /Cached after crash/ }),
		).toBeVisible({ timeout: 10_000 });
	} finally {
		if (!firstClosed) await terminateElectron(first.app);
		if (second) await second.app.close();
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
