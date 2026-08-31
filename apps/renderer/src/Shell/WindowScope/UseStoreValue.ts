import { useCallback, useSyncExternalStore } from "react";
import type Store from "react-granular-store";

/**
 * Subscribes to one key of a per-window store.
 *
 * `useSyncExternalStore` rather than an effect that calls `setState`: the store is an
 * external source, and reading it during render is what keeps a subscription from tearing —
 * a value that changed between render and the effect firing would otherwise be missed.
 */
export function useStoreValue<TState extends object, TKey extends keyof TState>(
	store: Store<TState>,
	key: TKey,
): TState[TKey] {
	const subscribe = useCallback(
		(onChange: () => void) => store.subscribe(key, onChange),
		[store, key],
	);

	return useSyncExternalStore(
		subscribe,
		() => store.getState(key),
		() => store.getState(key),
	);
}
