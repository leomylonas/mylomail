import { Menu, MenuItem, MenuItemDivider } from "@carbon/react";
import type { MenuAction } from "@mylomail/renderer/Shell/Registries/ContextMenus/ContextMenus";

/**
 * The conventional message menu (§13).
 *
 * Carbon's `Menu` rather than a hand-rolled one, because it carries the roving focus, arrow
 * navigation, escape handling and ARIA roles a right-click menu needs — all of which a custom
 * div quietly omits, and none of which are visible in a screenshot.
 */
export function MessageContextMenu({
	open,
	x,
	y,
	actions,
	onClose,
}: {
	open: boolean;
	x: number;
	y: number;
	actions: readonly MenuAction[];
	onClose: () => void;
}) {
	return (
		<Menu open={open} x={x} y={y} onClose={onClose} label="Message actions">
			{actions.map((action, index) =>
				action.label === "-" ? (
					// eslint-disable-next-line react/no-array-index-key
					<MenuItemDivider key={`divider-${index}`} />
				) : action.children ? (
					<MenuItem
						key={action.label}
						label={action.label}
						disabled={Boolean(action.unavailable)}
					>
						<Menu label={action.label}>
							{action.children.map((child) => (
								<MenuItem
									key={child.label}
									label={child.label}
									disabled={Boolean(child.unavailable)}
									kind={child.danger ? "danger" : "default"}
									onClick={child.run}
								/>
							))}
						</Menu>
					</MenuItem>
				) : (
					<MenuItem
						key={action.label}
						label={action.label}
						disabled={Boolean(action.unavailable)}
						kind={action.danger ? "danger" : "default"}
						onClick={action.run}
					/>
				),
			)}
		</Menu>
	);
}
