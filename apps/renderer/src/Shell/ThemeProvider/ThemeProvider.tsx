import { useQuery } from "@tanstack/react-query";
import { GlobalTheme, usePrefersDarkScheme } from "@carbon/react";

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
			const response = await fetch("/shell-settings");
			if (!response.ok) return { theme: themePreference.System };
			return (await response.json()) as { theme: number };
		},
	});

	const preference = settings.data?.theme ?? themePreference.System;
	const dark =
		preference === themePreference.Dark ||
		(preference === themePreference.System && prefersDark);

	return <GlobalTheme theme={dark ? "g100" : "white"}>{children}</GlobalTheme>;
}
