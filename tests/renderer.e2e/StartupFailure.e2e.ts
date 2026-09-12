import type { ChildProcess } from "node:child_process";
import { mkdtempSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { fileURLToPath } from "node:url";
import { _electron, expect, test } from "@playwright/test";

const repositoryRoot = fileURLToPath(new URL("../..", import.meta.url));

test("a backend migration failure is surfaced before Electron exits", async () => {
	const root = mkdtempSync(join(tmpdir(), "mylomail-startup-failure-"));
	const backend = join(root, "failing-backend.mjs");
	const marker = join(root, "dialog.json");
	writeFileSync(backend, "setTimeout(() => process.exit(1), 1500);\n");
	const environment = Object.fromEntries(
		Object.entries(process.env).filter(
			(entry): entry is [string, string] => entry[1] !== undefined,
		),
	);
	const app = await _electron.launch({
		args: [
			join(repositoryRoot, "apps/electron-shell/dist/Main.js"),
			"--headless",
			"--disable-gpu",
			"--no-sandbox",
		],
		env: {
			...environment,
			MYLOMAIL_BACKEND_COMMAND: process.execPath,
			MYLOMAIL_BACKEND_ARGS: backend,
		},
	});

	try {
		await app.evaluate(({ dialog }, markerPath) => {
			dialog.showErrorBox = (title, content) => {
				process
					.getBuiltinModule("node:fs")
					.writeFileSync(markerPath, JSON.stringify({ title, content }));
			};
		}, marker);
		await waitForExit(app.process());

		const shown = JSON.parse(readFileSync(marker, "utf8")) as {
			title: string;
			content: string;
		};
		expect(shown.title).toBe("MyloMail could not start");
		expect(shown.content).toContain("database upgrade fails");
		expect(shown.content).toContain(
			"Backend exited during startup with code 1.",
		);
	} finally {
		if (app.process().exitCode === null) await app.close();
		rmSync(root, { recursive: true, force: true });
	}
});

function waitForExit(child: ChildProcess): Promise<void> {
	if (child.exitCode !== null) return Promise.resolve();
	return new Promise((resolve, reject) => {
		child.once("exit", () => resolve());
		child.once("error", reject);
	});
}
