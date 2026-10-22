import { useState } from "react";
import {
	ArrowRight,
	Archive,
	Email,
	Flag,
	FolderMoveTo,
	OverflowMenuHorizontal,
	Printer,
	Reply,
	ReplyAll,
	TrashCan,
	Warning,
} from "@carbon/icons-react";
import { Button } from "@carbon/react";
import type { MenuAction } from "@mylomail/renderer/Shell/Registries/ContextMenus/ContextMenus";
import { MessageContextMenu } from "@mylomail/renderer/Shell/Registries/ContextMenus/MessageContextMenu/MessageContextMenu";
import styles from "@mylomail/renderer/Components/MessageCommands/MessageCommands.module.css";

type OpenMenu = {
	x: number;
	y: number;
	actions: readonly MenuAction[];
} | null;

/**
 * The message commands, in Outlook's order: Delete, Archive, Move to | Report junk | Reply,
 * Reply all, Forward | Read/Unread, Flag | Print | more. Shown in the app toolbar and in a message's
 * own window. Built from the same {@link MenuAction} list as the right-click menu, so
 * the two can never disagree about what a command does or when it is unavailable.
 */
export function MessageCommands({
	actions,
	disabled,
}: {
	actions: readonly MenuAction[];
	disabled: boolean;
}) {
	const [menu, setMenu] = useState<OpenMenu>(null);
	const byLabel = (prefix: string) =>
		actions.find((action) => action.label.startsWith(prefix));
	const trash = byLabel("Move to trash");
	const reply = actions.find((action) => action.label === "Reply");
	const replyAll = byLabel("Reply all");
	const forward = byLabel("Forward");
	const read = byLabel("Mark");
	const flag = actions.find(
		(action) =>
			action.label.startsWith("Flag") || action.label.startsWith("Remove flag"),
	);
	const archive = byLabel("Archive");
	const junk = byLabel("Report junk");
	const print = byLabel("Print");
	// Folders of this account first; one more entry per other account, each with its own folders.
	const moves = actions.filter(
		(action) => action.children && action.label.startsWith("Move to"),
	);
	const move = moves[0];
	const overflow = actions.filter((action) =>
		["Delete permanently", "Save as .eml"].some((prefix) =>
			action.label.startsWith(prefix),
		),
	);

	const command = (
		label: string,
		icon: typeof Reply,
		action: MenuAction | undefined,
	) => (
		<Button
			className={styles.command}
			size="sm"
			kind="ghost"
			renderIcon={icon}
			disabled={disabled || !action || Boolean(action.unavailable)}
			title={action?.unavailable}
			onClick={() => action?.run()}
		>
			{label}
		</Button>
	);

	const moveTo = (
		<Button
			className={styles.command}
			size="sm"
			kind="ghost"
			renderIcon={FolderMoveTo}
			disabled={disabled || !move || Boolean(move.unavailable)}
			title={move?.unavailable}
			onClick={(event: React.MouseEvent<HTMLButtonElement>) => {
				const rect = event.currentTarget.getBoundingClientRect();
				setMenu({
					x: rect.left,
					y: rect.bottom,
					actions: moves.length > 1 ? moves : (move?.children ?? []),
				});
			}}
		>
			Move to
		</Button>
	);

	return (
		<>
			{command("Delete", TrashCan, trash)}
			{command("Archive", Archive, archive)}
			{moveTo}
			<span className={styles.divider} aria-hidden="true" />
			{command("Report junk", Warning, junk)}
			<span className={styles.divider} aria-hidden="true" />
			{command("Reply", Reply, reply)}
			{command("Reply all", ReplyAll, replyAll)}
			{command("Forward", ArrowRight, forward)}
			<span className={styles.divider} aria-hidden="true" />
			{command(
				read?.label.startsWith("Mark unread") ? "Mark unread" : "Mark read",
				Email,
				read,
			)}
			{command(
				flag?.label.startsWith("Remove") ? "Remove flag" : "Flag",
				Flag,
				flag,
			)}
			<span className={styles.divider} aria-hidden="true" />
			{command("Print", Printer, print)}
			<Button
				size="sm"
				kind="ghost"
				hasIconOnly
				renderIcon={OverflowMenuHorizontal}
				iconDescription="More message actions"
				tooltipPosition="bottom"
				disabled={disabled || overflow.length === 0}
				onClick={(event: React.MouseEvent<HTMLButtonElement>) => {
					const rect = event.currentTarget.getBoundingClientRect();
					setMenu({ x: rect.left, y: rect.bottom, actions: overflow });
				}}
			/>
			{menu ? (
				<MessageContextMenu
					open
					x={menu.x}
					y={menu.y}
					actions={menu.actions}
					onClose={() => setMenu(null)}
				/>
			) : null}
		</>
	);
}
