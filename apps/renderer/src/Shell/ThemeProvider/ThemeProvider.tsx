import { useEffect } from "react";
import { useQuery } from "@tanstack/react-query";
import { GlobalTheme, usePrefersDarkScheme } from "@carbon/react";
import { fetchApi } from "@mylomail/renderer/Shell/Backend/ProblemDetailsTransport";

/** Matches server/MyloMail.Api/Domain/AppSettings.cs's ThemePreference enum ordinals — this
 * shell reads it over REST rather than through the typed SignalR hub, so System.Text.Json's
 * default numeric enum serialisation applies (no JsonStringEnumConverter registered). */
const themePreference = { System: 0, Light: 1, Dark: 2 } as const;

/**
 * Applies the persisted `AppSettings.Theme` (§13 Epic 8) to every window via Carbon's
 * `GlobalTheme`, resolving `System` through the OS `prefers-color-scheme` media query rather
 * than a fixed default.
 */
export function ThemeProvider({ children }: { children: React.ReactNode }) {
	const prefersDark = usePrefersDarkScheme();
	const settings = useQuery({
		// Its own key, not "shell-settings" — useShellLayout caches a differently-shaped
		// result under that key, and TanStack Query would otherwise return whichever hook's
		// queryFn happened to run first to both.
		queryKey: ["shell-settings", "theme"],
		queryFn: async (): Promise<{ theme: number }> => {
			const response = await fetchApi("/shell-settings");
			return (await response.json()) as { theme: number };
		},
	});

	const preference = settings.data?.theme ?? themePreference.System;
	const dark =
		preference === themePreference.Dark ||
		(preference === themePreference.System && prefersDark);

	const theme = dark ? "g100" : "white";

	// `GlobalTheme` only feeds Carbon's React context. The design tokens themselves are CSS
	// custom properties scoped to a `cds--<theme>` class, so without the class on the root
	// every `var(--cds-*)` in the app — including in portalled menus and native dialogs,
	// which sit outside any wrapper element — resolves to nothing.
	useEffect(() => {
		const root = document.documentElement;
		// A page is printed on white paper whatever the screen theme: dark tokens would put pale
		// text on it and, in a dark theme, a dark page background into the PDF.
		const printing = window.matchMedia("print");
		const apply = () => {
			root.classList.remove("cds--white", "cds--g100");
			root.classList.add(
				`cds--${printing.matches ? "white" : theme}`,
				"cds--layer-one",
			);
		};
		apply();
		printing.addEventListener("change", apply);
		return () => printing.removeEventListener("change", apply);
	}, [theme]);

	return <GlobalTheme theme={theme}>{children}</GlobalTheme>;
}
