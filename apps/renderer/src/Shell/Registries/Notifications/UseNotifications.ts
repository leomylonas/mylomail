import { createContext, useContext } from "react";
import type Store from "react-granular-store";
import { useStoreValue } from "@mylomail/renderer/Shell/WindowScope/UseStoreValue";
import type { NotificationState } from "@mylomail/renderer/Shell/Registries/Notifications/NotificationStore";
import type { AppNotification } from "@mylomail/renderer/Shell/Registries/Notifications/Notifications";

export const NotificationStoreContext =
	createContext<Store<NotificationState> | null>(null);

export function useWindowNotifications(): {
	store: Store<NotificationState>;
	notifications: AppNotification[];
} {
	const store = useContext(NotificationStoreContext);
	if (!store)
		throw new Error("Notifications must be used inside a WindowScope.");

	return { store, notifications: useStoreValue(store, "notifications") };
}
