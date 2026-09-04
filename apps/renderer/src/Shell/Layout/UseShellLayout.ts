import { useEffect, useRef } from "react";
import { useQuery } from "@tanstack/react-query";

export interface PanelLayout {
	sidebar: number;
	list: number;
	detail: number;
}

const defaultLayout: PanelLayout = { sidebar: 20, list: 35, detail: 45 };

/**
 * The global default panel layout (§13 Epic 11): read once when this window opens, written
 * back whenever the user resizes a panel so the *next* window opened inherits it. Deliberately
 * not synced to any window already open — each window's own live sizes stay in
 * `react-resizable-panels`' own component state, never mirrored back here.
 */
export function useShellLayout(onSaveError?: (error: unknown) => void): {
	initial: PanelLayout;
	ready: boolean;
	onResize: (layout: PanelLayout) => void;
} {
	const query = useQuery({
		queryKey: ["shell-settings"],
		queryFn: async (): Promise<PanelLayout> => {
			const response = await fetch("/shell-settings");
			if (!response.ok) return defaultLayout;
			const settings = (await response.json()) as {
				panelLayout?: string | null;
			};
			if (!settings.panelLayout) return defaultLayout;
			try {
				return {
					...defaultLayout,
					...(JSON.parse(settings.panelLayout) as object),
				};
			} catch {
				return defaultLayout;
			}
		},
	});

	const timer = useRef<ReturnType<typeof setTimeout> | undefined>(undefined);
	useEffect(() => () => clearTimeout(timer.current), []);

	const save = async (layout: PanelLayout) => {
		try {
			const response = await fetch("/shell-settings/panel-layout", {
				method: "PUT",
				headers: { "Content-Type": "application/json" },
				body: JSON.stringify({ panelLayout: JSON.stringify(layout) }),
			});
			if (!response.ok) {
				throw new Error(`panel-layout responded ${response.status}`);
			}
		} catch (error) {
			onSaveError?.(error);
		}
	};

	const onResize = (layout: PanelLayout) => {
		clearTimeout(timer.current);
		timer.current = setTimeout(() => void save(layout), 500);
	};

	return {
		initial: query.data ?? defaultLayout,
		ready: !query.isPending,
		onResize,
	};
}
