import { afterEach, expect, test } from "vitest";
import {
	mkdirSync,
	mkdtempSync,
	readFileSync,
	rmSync,
	writeFileSync,
} from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import {
	developmentDesktopName,
	installDevelopmentDesktop,
} from "./DevelopmentDesktop";

const temporaryDirectories: string[] = [];

afterEach(() => {
	for (const directory of temporaryDirectories.splice(0))
		rmSync(directory, { force: true, recursive: true });
});

test("developer launcher uses XDG applications without replacing the release launcher", () => {
	const dataHome = temporaryDirectory();
	const applications = join(dataHome, "applications");
	const releaseLauncher = join(applications, "MyloMail.desktop");
	const root = "/work/mylo-mail";

	mkdirSync(applications);
	const releaseContents = "[Desktop Entry]\nName=Mylo Mail\n";
	writeFileSync(releaseLauncher, releaseContents);
	const desktopPath = installDevelopmentDesktop(root, {
		XDG_DATA_HOME: dataHome,
		PATH: "/tool chain/bin",
		MYLOMAIL_PROVIDER_SECRET: "not for the desktop entry",
	});

	expect(desktopPath).toBe(join(applications, developmentDesktopName));
	expect(readFileSync(releaseLauncher, "utf8")).toBe(releaseContents);
	expect(readFileSync(desktopPath, "utf8")).not.toContain(
		"not for the desktop entry",
	);
});

test("a relative XDG data home falls back to the user's local share directory", () => {
	const home = temporaryDirectory();
	const desktopPath = installDevelopmentDesktop(
		"/work/mylo-mail",
		{ XDG_DATA_HOME: "relative-data-home" },
		home,
	);

	expect(desktopPath).toBe(
		join(home, ".local", "share", "applications", developmentDesktopName),
	);
});

function temporaryDirectory(): string {
	const directory = mkdtempSync(
		join(tmpdir(), "mylomail-development-desktop-"),
	);
	temporaryDirectories.push(directory);
	return directory;
}
