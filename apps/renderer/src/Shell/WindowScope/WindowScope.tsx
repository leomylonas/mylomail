import { createContext, useContext, useMemo, type ReactNode } from "react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import type Store from "react-granular-store";
import {
	createWindowStore,
	type WindowState,
} from "@mylomail/renderer/Shell/WindowScope/WindowStore";

const WindowStoreContext = createContext<Store<WindowState> | null>(null);

/**
 * Everything scoped to one window: its UI state and its query cache.
 *
 * The query client is per window for the same reason as the store. Sharing one would mean a
 * refetch triggered by one window's navigation quietly changed what another window was
 * showing, and cache invalidation on reconnect would apply to queries no visible component
 * had asked for.
 */
export function WindowScope({ children }: { children: ReactNode }) {
	const store = useMemo(() => createWindowStore(), []);
	const queryClient = useMemo(
		() =>
			new QueryClient({
				defaultOptions: {
					queries: {
						// The backend is local and pushes changes over the hub, so polling adds
						// nothing: freshness comes from event-driven invalidation, not a timer.
						refetchOnWindowFocus: false,
						staleTime: Infinity,
						retry: false,
					},
				},
			}),
		[],
	);

	return (
		<QueryClientProvider client={queryClient}>
			<WindowStoreContext.Provider value={store}>
				{children}
			</WindowStoreContext.Provider>
		</QueryClientProvider>
	);
}

export function useWindowStore(): Store<WindowState> {
	const store = useContext(WindowStoreContext);
	if (!store)
		throw new Error("useWindowStore must be used inside a WindowScope.");
	return store;
}
