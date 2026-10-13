import { createContext, useContext, useMemo } from "react";
import { useQuery } from "@tanstack/react-query";
import { fetchApi } from "@mylomail/renderer/Shell/Backend/ProblemDetailsTransport";
import {
	clockPreferenceFromSetting,
	createDateFormats,
	systemDateFormats,
	type DateFormats,
} from "@mylomail/renderer/Lib/DateFormats";

const DateFormatsContext = createContext<DateFormats>(systemDateFormats);

/**
 * Supplies the persisted 12/24-hour preference (`AppSettings.TimeFormat`) to every component that
 * shows a time, so a change in Settings reaches every window and every date and time at once.
 */
export function DateFormatsProvider({
	children,
}: {
	children: React.ReactNode;
}) {
	const settings = useQuery({
		// Its own key, for the reason ThemeProvider gives for its own.
		queryKey: ["shell-settings", "time-format"],
		queryFn: async (): Promise<{ timeFormat: number }> => {
			const response = await fetchApi("/shell-settings");
			return (await response.json()) as { timeFormat: number };
		},
	});
	const formats = useMemo(
		() =>
			createDateFormats(clockPreferenceFromSetting(settings.data?.timeFormat)),
		[settings.data?.timeFormat],
	);
	return (
		<DateFormatsContext.Provider value={formats}>
			{children}
		</DateFormatsContext.Provider>
	);
}

export function useDateFormats(): DateFormats {
	return useContext(DateFormatsContext);
}
