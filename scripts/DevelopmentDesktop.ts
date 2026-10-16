import { mkdirSync, writeFileSync } from "node:fs";
import { homedir } from "node:os";
import { isAbsolute, join } from "node:path";

export const developmentDesktopId = "com.mylomail.desktop.dev";
export const developmentDesktopName = `${developmentDesktopId}.desktop`;

/**
 * Installs the checkout-owned launcher that lets GNOME associate development Electron
 * windows with this source tree without changing the packaged application's launcher.
 */
export function installDevelopmentDesktop(
	root: string,
	environment: NodeJS.ProcessEnv = process.env,
	homeDirectory: string = homedir(),
): string {
	const applicationsDirectory = developmentApplicationsDirectory(
		environment,
		homeDirectory,
	);
	mkdirSync(applicationsDirectory, { recursive: true });

	const desktopPath = join(applicationsDirectory, developmentDesktopName);
	writeFileSync(desktopPath, createDevelopmentDesktopEntry(root, environment));
	return desktopPath;
}

export function createDevelopmentDesktopEntry(
	root: string,
	environment: NodeJS.ProcessEnv = process.env,
): string {
	const node = process.execPath;
	const tsx = join(root, "node_modules", "tsx", "dist", "cli.mjs");
	const launcher = join(root, "scripts", "LaunchApp.ts");
	const icon = join(root, "assets", "icons", "app-icon.png");
	const path = environment.PATH ?? "";

	return [
		"[Desktop Entry]",
		"Type=Application",
		"Name=Mylo Mail (Development)",
		`Icon=${escapeDesktopValue(icon)}`,
		`Exec=${["/usr/bin/env", `PATH=${path}`, node, tsx, launcher]
			.map(escapeExecArgument)
			.join(" ")} %U`,
		`Path=${escapeDesktopValue(root)}`,
		"Terminal=false",
		`StartupWMClass=${developmentDesktopId}`,
		"",
	].join("\n");
}

function developmentApplicationsDirectory(
	environment: NodeJS.ProcessEnv,
	homeDirectory: string,
): string {
	const dataHome = environment.XDG_DATA_HOME;
	return dataHome && isAbsolute(dataHome)
		? join(dataHome, "applications")
		: join(homeDirectory, ".local", "share", "applications");
}

function escapeDesktopValue(value: string): string {
	return value
		.replaceAll("\\", "\\\\")
		.replaceAll("\n", "\\n")
		.replaceAll("\r", "\\r")
		.replaceAll("\t", "\\t");
}

function escapeExecArgument(argument: string): string {
	// The key-file parser unescapes first; Exec quoting is a second, separate layer.
	return escapeDesktopValue(
		`"${argument.replace(/(["\\$`])/g, "\\$1").replaceAll("%", "%%")}"`,
	);
}
