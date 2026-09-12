import { ActionableNotification, ToastNotification } from "@carbon/react";
import { useWindowNotifications } from "@mylomail/renderer/Shell/Registries/Notifications/UseNotifications";
import { dismiss } from "@mylomail/renderer/Shell/Registries/Notifications/NotificationStore";
import styles from "@mylomail/renderer/Shell/Registries/Notifications/NotificationArea/NotificationArea.module.css";

/**
 * Renders this window's notifications.
 *
 * Carbon's two components are not interchangeable: `ToastNotification` auto-dismisses and must
 * carry no interactive content, and `ActionableNotification` persists and may. Choosing by
 * whether the notification has an action is what keeps that rule true by construction rather
 * than by review (§13).
 */
export function NotificationArea() {
	const { store, notifications } = useWindowNotifications();

	return (
		<div className={styles.area} role="region" aria-label="Notifications">
			{notifications.map((notification) =>
				notification.action ? (
					<ActionableNotification
						key={notification.id}
						kind={notification.kind}
						title={notification.title}
						subtitle={notification.detail}
						actionButtonLabel={notification.action.label}
						onActionButtonClick={notification.action.run}
						onClose={() => dismiss(store, notification.id)}
						lowContrast
					/>
				) : (
					<ToastNotification
						key={notification.id}
						kind={notification.kind}
						title={notification.title}
						subtitle={notification.detail}
						timeout={notification.persistent ? 0 : 6000}
						onClose={() => dismiss(store, notification.id)}
						lowContrast
					/>
				),
			)}
		</div>
	);
}
