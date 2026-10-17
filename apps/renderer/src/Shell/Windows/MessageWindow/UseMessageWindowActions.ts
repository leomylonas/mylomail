import { useQuery, useQueryClient } from "@tanstack/react-query";
import type { MailHubConnection } from "@mylomail/renderer/Shell/Backend/HubConnection";
import { notificationForError } from "@mylomail/renderer/Shell/Backend/ProblemDetailsTransport";
import { notify } from "@mylomail/renderer/Shell/Registries/Notifications/NotificationStore";
import { useWindowNotifications } from "@mylomail/renderer/Shell/Registries/Notifications/UseNotifications";
import type { MenuAction } from "@mylomail/renderer/Shell/Registries/ContextMenus/ContextMenus";
import { mailboxesQuery } from "@mylomail/renderer/Components/MailboxTree/MailboxTree";
import { saveAsEml } from "@mylomail/renderer/Components/MessageList/MessageList";

/**
 * What a popped-out message window can do to its one message, so it offers everything the main
 * window's toolbar and message menu do: delete, mark read or unread, flag, move, delete for good,
 * save. `onDelete` is the toolbar button; `menu` goes into the overflow menu.
 */
export function useMessageWindowActions({
	hub,
	messageId,
	accountId,
	subject,
}: {
	hub: MailHubConnection | null;
	messageId: string;
	accountId: string | undefined;
	subject: string;
}): { onDelete: () => void; menu: MenuAction[] } {
	const { store: notifications } = useWindowNotifications();
	const queryClient = useQueryClient();
	const state = useQuery({
		queryKey: ["message-state", messageId],
		queryFn: () => hub!.getMessageState(messageId),
		enabled: hub !== null,
	});
	const mailboxes = useQuery({
		...mailboxesQuery(hub as MailHubConnection, accountId ?? ""),
		enabled: hub !== null && accountId !== undefined,
	});

	const run = (
		title: string,
		work: () => Promise<unknown>,
		thenClose = false,
	) =>
		void work()
			.then(() => {
				void queryClient.invalidateQueries({
					queryKey: ["message-state", messageId],
				});
				void queryClient.invalidateQueries({ queryKey: ["messages"] });
				if (thenClose) window.close();
			})
			.catch((error: unknown) =>
				notify(notifications, notificationForError(error, title)),
			);

	const noAccount =
		accountId === undefined
			? "This window doesn't know the message's account."
			: undefined;
	const toTrash = () =>
		hub !== null &&
		accountId !== undefined &&
		run(
			"The message could not be deleted",
			() => hub!.moveToTrash(accountId, [messageId]),
			true,
		);

	const isRead = state.data?.isRead ?? true;
	const isFlagged = state.data?.isFlagged ?? false;
	const folders = (mailboxes.data ?? [])
		.filter((mailbox) => !mailbox.isSynthesized)
		.sort((a, b) => a.name.localeCompare(b.name));

	return {
		onDelete: toTrash,
		menu: [
			{
				label: isRead ? "Mark unread" : "Mark read",
				run: () =>
					hub !== null &&
					accountId !== undefined &&
					run("The message could not be updated", () =>
						hub!.setFlags(accountId, [messageId], !isRead, undefined),
					),
				unavailable: noAccount,
			},
			{
				label: isFlagged ? "Remove flag" : "Flag",
				run: () =>
					hub !== null &&
					accountId !== undefined &&
					run("The message could not be updated", () =>
						hub!.setFlags(accountId, [messageId], undefined, !isFlagged),
					),
				unavailable: noAccount,
			},
			{
				label: "Move to",
				run: () => undefined,
				unavailable:
					noAccount ??
					(folders.length === 0 ? "No other folders yet." : undefined),
				children: folders.map((folder) => ({
					label: folder.name,
					run: () =>
						hub !== null &&
						accountId !== undefined &&
						run("The message could not be moved", () =>
							hub!.moveMessages(accountId, [messageId], folder.id),
						),
				})),
			},
			{ label: "-", run: () => undefined },
			{
				label: "Delete permanently",
				danger: true,
				run: () => {
					if (
						hub !== null &&
						accountId !== undefined &&
						window.confirm(
							"Permanently delete this message? This cannot be undone.",
						)
					) {
						run(
							"The message could not be deleted",
							() => hub!.deletePermanently(accountId, [messageId]),
							true,
						);
					}
				},
				unavailable: noAccount,
			},
			{
				label: "Save as .eml",
				run: () =>
					hub !== null &&
					void saveAsEml(hub, { id: messageId, subject }).catch(
						(error: unknown) =>
							notify(
								notifications,
								notificationForError(error, "The message could not be saved"),
							),
					),
			},
			{ label: "-", run: () => undefined },
		],
	};
}
