import Store from "react-granular-store";
import type { AppNotification } from "@mylomail/renderer/Shell/Registries/Notifications/Notifications";

export interface NotificationState {
	notifications: AppNotification[];
}

/**
 * The window's notifications.
 *
 * Per window like every other store here: a failure belongs to the window whose action caused
 * it, and raising it in all of them would have a background window announce something the
 * user is not looking at.
 */
export function createNotificationStore(): Store<NotificationState> {
	return new Store<NotificationState>({ notifications: [] });
}

export function notify(
	store: Store<NotificationState>,
	notification: Omit<AppNotification, "id">,
): void {
	const existing = store.getState("notifications");

	// Collapsed by title: a provider that rejects fifty messages in one batch produces fifty
	// identical failures, and fifty identical toasts is not fifty times the information.
	if (existing.some((candidate) => candidate.title === notification.title))
		return;

	store.setState("notifications", [
		...existing,
		{ ...notification, id: crypto.randomUUID() },
	]);
}

export function dismiss(store: Store<NotificationState>, id: string): void {
	store.setState(
		"notifications",
		store
			.getState("notifications")
			.filter((notification) => notification.id !== id),
	);
}
