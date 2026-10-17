import { copyFileSync, mkdirSync } from "node:fs";
import { createRequire } from "node:module";
import { dirname, join, resolve } from "node:path";
import { spawn } from "node:child_process";

const require = createRequire(import.meta.url);
const website = resolve(import.meta.dirname, "..");
const repository = resolve(website, "..");
const brand = join(website, "public", "Brand");
const fonts = join(website, "public", "Fonts");
mkdirSync(brand, { recursive: true });
mkdirSync(fonts, { recursive: true });

// The approved artwork remains the source of truth; generated copies are not committed.
for (const [source, destination] of [
	["horizontal-light.svg", "HorizontalLight.svg"],
	["horizontal-dark.svg", "HorizontalDark.svg"],
	["mark-light.svg", "MarkLight.svg"],
	["mark-dark.svg", "MarkDark.svg"],
]) {
	copyFileSync(
		join(repository, "docs", "branding", "logos", source),
		join(brand, destination),
	);
}
copyFileSync(
	join(repository, "assets", "icons", "app-icon.png"),
	join(brand, "Icon.png"),
);
copyFileSync(
	join(repository, "assets", "icons", "app-icon.svg"),
	join(brand, "Icon.svg"),
);
copyFileSync(
	join(repository, "assets", "icons", "app-icon.ico"),
	join(website, "public", "favicon.ico"),
);

const plex = dirname(require.resolve("@ibm/plex/package.json"));
for (const weight of ["Regular", "SemiBold"]) {
	const name = `IBMPlexSans-${weight}.woff2`;
	copyFileSync(
		join(plex, "IBM-Plex-Sans", "fonts", "complete", "woff2", name),
		join(fonts, name),
	);
}
copyFileSync(join(plex, "LICENSE.txt"), join(fonts, "License.txt"));

const child = spawn(
	process.execPath,
	[require.resolve("next/dist/bin/next"), ...process.argv.slice(2)],
	{
		cwd: website,
		stdio: "inherit",
		env: { ...process.env, NEXT_TELEMETRY_DISABLED: "1" },
	},
);
for (const signal of ["SIGINT", "SIGTERM", "SIGHUP"] as const) {
	process.on(signal, () => child.kill(signal));
}
child.on("error", (error) => {
	console.error(error);
	process.exitCode = 1;
});
child.on("exit", (code, signal) => {
	process.exitCode = code ?? (signal === "SIGINT" ? 130 : 1);
});
