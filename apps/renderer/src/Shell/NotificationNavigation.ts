import type { NotificationNavigationDto } from "@mylomail/shared-types/SignalR/MyloMail.Api.Contracts";
import { NotificationNavigationStatus } from "@mylomail/shared-types/SignalR/MyloMail.Api.Domain";
import type Store from "react-granular-store";
import type { WindowState } from "@mylomail/renderer/Shell/WindowScope/WindowStore";

type ResolveNavigation = (
	notificationId: string,
) => Promise<NotificationNavigationDto | null>;
type Wait = (milliseconds: number, signal: AbortSignal) => Promise<void>;

export type ReadyNotificationNavigation = NotificationNavigationDto & {
	status: NotificationNavigationStatus.Ready;
	mailboxId: string;
	messageId: string;
};
/**
 * Replays a still-staged notification until it has canonical local identity to navigate to.
 * Coverage can legitimately keep Gmail history staged for hours; treating the first Pending
 * answer as a missing message would make the architecture's on-demand click path fictitious.
 */
export async function waitForNotificationNavigation(
	resolve: ResolveNavigation,
	notificationId: string,
	signal: AbortSignal,
	onPending: () => void,
	wait: Wait = waitForDelay,
): Promise<NotificationNavigationDto | null> {
	let delay = 250;
	let reportedPending = false;
	while (!signal.aborted) {
		const navigation = await resolve(notificationId);
		if (
			navigation === null ||
			navigation.status === NotificationNavigationStatus.Ready
		) {
			return navigation;
		}

		if (!reportedPending) {
			reportedPending = true;
			onPending();
		}
		await wait(delay, signal);
		delay = Math.min(delay * 2, 30_000);
	}
	return null;
}

/** Applies all message context atomically from one canonical backend projection. */
export function applyNotificationNavigation(
	store: Store<WindowState>,
	navigation: NotificationNavigationDto,
): navigation is ReadyNotificationNavigation {
	if (
		navigation.status !== NotificationNavigationStatus.Ready ||
		!navigation.mailboxId ||
		!navigation.messageId
	) {
		return false;
	}

	store.setState("selectedAccountId", navigation.accountId);
	store.setState("selectedMailboxId", navigation.mailboxId);
	store.setState("selectedMessageId", navigation.messageId);
	store.setState("selectedMessageSubject", navigation.subject);
	store.setState("selectedMessageSenderAddress", navigation.senderAddress);
	return true;
}

function waitForDelay(
	milliseconds: number,
	signal: AbortSignal,
): Promise<void> {
	// DOM timers and AbortSignal are callback APIs; this executor is the bridge between them.
	return new Promise((resolve) => {
		if (signal.aborted) {
			resolve();
			return;
		}

		const aborted = () => {
			clearTimeout(timer);
			resolve();
		};
		const timer = setTimeout(() => {
			signal.removeEventListener("abort", aborted);
			resolve();
		}, milliseconds);
		signal.addEventListener("abort", aborted, { once: true });
	});
}
