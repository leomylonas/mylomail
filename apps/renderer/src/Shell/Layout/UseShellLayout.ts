import { useQuery } from "@tanstack/react-query";
import { fetchApi } from "@mylomail/renderer/Shell/Backend/ProblemDetailsTransport";

export interface PanelLayout {
	sidebar: number;
	list: number;
	detail: number;
}

const defaultLayout: PanelLayout = { sidebar: 20, list: 35, detail: 45 };

async function saveLayout(
	layout: PanelLayout,
	onSaveError?: (error: unknown) => void,
): Promise<void> {
	try {
		const panelLayout = JSON.stringify(layout);
		if (window.shellSettings) {
			await window.shellSettings.savePanelLayout(panelLayout);
			return;
		}
		await fetchApi("/shell-settings/panel-layout", {
			method: "PUT",
			headers: { "Content-Type": "application/json" },
			body: JSON.stringify({ panelLayout }),
			keepalive: true,
		});
	} catch (error) {
		onSaveError?.(error);
	}
}

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
			const response = await fetchApi("/shell-settings");
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

	const onResize = (layout: PanelLayout) => {
		// Group.onLayoutChanged fires once after pointer release (and once per completed
		// keyboard resize), so a second debounce only creates a data-loss window. Dispatch
		// the final layout immediately; keepalive lets that request complete during teardown.
		void saveLayout(layout, onSaveError);
	};

	return {
		initial: query.data ?? defaultLayout,
		ready: !query.isPending,
		onResize,
	};
}
