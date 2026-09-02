import { useRef, useState } from "react";
import {
	useMutation,
	useQuery,
	useQueryClient,
	type QueryClient,
} from "@tanstack/react-query";
import {
	flexRender,
	type SortingState,
	type FilterFn,
} from "@tanstack/react-table";
// TanStack Table v9 replaced the v8 `useReactTable`/`createColumnHelper` API with a new
// `useTable`/`createTableHook` paradigm; the `/legacy` subpath is the library's own official
// v8-compatibility shim (deprecated, but a maintained export, not a hack) and is used here
// deliberately so this reads exactly like the well-documented v8 API rather than the very new
// v9 one. Migrating to `useTable` is a reasonable future cleanup, not required for correctness.
import {
	useLegacyTable as useReactTable,
	getCoreRowModel,
	getSortedRowModel,
	getFilteredRowModel,
	legacyCreateColumnHelper as createColumnHelper,
} from "@tanstack/react-table/legacy";
import { useVirtualizer } from "@tanstack/react-virtual";
import type { HubConnection } from "@microsoft/signalr";
import { SkeletonText, TextInput } from "@carbon/react";
import { queryKeys } from "@mylomail/renderer/Shell/Backend/HubConnection";
import { MessageContextMenu } from "@mylomail/renderer/Shell/Registries/ContextMenus/MessageContextMenu/MessageContextMenu";
import type { MenuAction } from "@mylomail/renderer/Shell/Registries/ContextMenus/ContextMenus";
import { useShortcuts } from "@mylomail/renderer/Shell/Registries/Shortcuts/UseShortcuts";
import { messageDragType } from "@mylomail/renderer/Lib/DragTypes";
import styles from "@mylomail/renderer/Components/MessageList/MessageList.module.css";

interface MessageSummary {
	id: string;
	subject: string;
	snippet: string;
	from: { name: string | null; email: string }[];
	receivedAt: string;
	isRead: boolean;
	isFlagged: boolean;
	hasNonInlineAttachments: boolean;
}

interface PendingChange {
	messageId: string;
	field: number;
	desiredValue: boolean;
}

const isReadField = 0;

const columnHelper = createColumnHelper<MessageSummary>();

/**
 * Row height fed to the virtualizer as an estimate (§12). Rows in this list are all the same
 * height — unlike the calendar agenda's day rows, which vary with event count — so this is a
 * constant rather than a per-row measurement, and `measureElement` still corrects it if a row
 * ever does render taller (e.g. a very long wrapped subject).
 */
const rowHeightEstimate = 64;

/**
 * Matches across the fields the row itself displays, not full-text search: this is a fast,
 * local narrowing of whatever page of messages is already loaded, distinct from the `Search`
 * hub method the search box already triggers server-side (§12, §13 Epic 6).
 */
const globalFilterFn: FilterFn<MessageSummary> = (
	row,
	_columnId,
	filterValue,
) => {
	const needle = String(filterValue).trim().toLowerCase();
	if (!needle) return true;
	const message = row.original;
	return (
		message.subject.toLowerCase().includes(needle) ||
		message.snippet.toLowerCase().includes(needle) ||
		describeSender(message).toLowerCase().includes(needle)
	);
};

const columns = [
	columnHelper.accessor((row) => describeSender(row), {
		id: "from",
		header: "From",
	}),
	columnHelper.accessor("subject", {
		id: "subject",
		header: "Subject",
	}),
	columnHelper.accessor("receivedAt", {
		id: "receivedAt",
		header: "Date",
		// ISO timestamps sort correctly as strings only when every value shares the same
		// offset convention; comparing parsed instants is correct regardless of how the
		// server serialised the offset.
		sortingFn: (a, b) =>
			new Date(a.original.receivedAt).getTime() -
			new Date(b.original.receivedAt).getTime(),
	}),
];

export function MessageList({
	hub,
	accountId,
	mailboxId,
	query,
	onSelect,
	onPrint,
}: {
	hub: HubConnection;
	accountId: string;
	mailboxId: string;
	query: string;
	onSelect: (message: { id: string; subject: string; from: string }) => void;
	/**
	 * Opens the message in the reading pane, the same as {@link onSelect}, but the caller
	 * additionally switches to it: printing has to go through the reading pane's own sandboxed
	 * `MessageHtml` rendering rather than a second, ad-hoc render path for remote-authored
	 * content (§13).
	 */
	onPrint: (message: { id: string; subject: string; from: string }) => void;
}) {
	const queryClient = useQueryClient();
	const searching = query.trim().length > 0;
	const [menu, setMenu] = useState<{
		x: number;
		y: number;
		targets: MessageSummary[];
	} | null>(null);

	// Multi-select (§13 Epic 6): shift/ctrl/cmd-click extend it, a plain click collapses it to
	// one row. Ids rather than objects, so the set survives a refetch that returns new message
	// object identities for the same messages.
	const [selectedIds, setSelectedIds] = useState<ReadonlySet<string>>(
		new Set(),
	);
	const [anchorIndex, setAnchorIndex] = useState<number | null>(null);

	// Outlook-style sortable columns and a local filter (§12) — client-side over whatever page
	// is already loaded, not a new server round trip.
	const [sorting, setSorting] = useState<SortingState>([]);
	const [filterText, setFilterText] = useState("");

	// One list, two sources. Searching scopes to the selected mailbox, because a search from
	// inside a folder that silently returned results from everywhere would be a different
	// question than the one the user asked.
	const messages = useQuery({
		queryKey: searching
			? queryKeys.search(accountId, query, mailboxId)
			: queryKeys.messages(mailboxId),
		queryFn: () =>
			searching
				? hub.invoke<MessageSummary[]>("Search", accountId, query, mailboxId)
				: hub.invoke<MessageSummary[]>("GetMessages", mailboxId, 100),
	});

	// What the user has asked for and the server has not yet confirmed. Merged over
	// server-known state so a flag they just toggled does not flicker back while its mutation
	// is in flight (§6).
	const pending = useQuery({
		queryKey: queryKeys.pending(accountId),
		queryFn: () =>
			hub.invoke<PendingChange[]>("GetPendingSyncState", accountId),
	});

	// Bulk by construction: every caller passes the whole target set, one message included,
	// rather than this component looping — a single hub call per action either way, since
	// SetFlags/MoveToTrash already take an id list (§13 Epic 6).
	const setFlags = useMutation({
		mutationFn: ({
			messages,
			isRead,
			isFlagged,
		}: {
			messages: MessageSummary[];
			isRead: boolean | null;
			isFlagged: boolean | null;
		}) =>
			hub.invoke(
				"SetFlags",
				accountId,
				messages.map((message) => message.id),
				isRead,
				isFlagged,
			),
		onSettled: () =>
			queryClient.invalidateQueries({ queryKey: queryKeys.pending(accountId) }),
	});

	const trash = useMutation({
		mutationFn: (messages: MessageSummary[]) =>
			hub.invoke(
				"MoveToTrash",
				accountId,
				messages.map((message) => message.id),
			),
		onSettled: () => queryClient.invalidateQueries({ queryKey: ["messages"] }),
	});

	// Message-scoped and never reversible by the app (§6) — unlike MoveToTrash, which the
	// provider's own trash still lets the user recover from.
	const deletePermanently = useMutation({
		mutationFn: (messages: MessageSummary[]) =>
			hub.invoke(
				"DeletePermanently",
				accountId,
				messages.map((message) => message.id),
			),
		onSettled: () => queryClient.invalidateQueries({ queryKey: ["messages"] }),
	});

	// Selection tracks the full, unfiltered list: a message shift/ctrl-selected before a local
	// filter narrowed the view stays selected, so a bulk action or shortcut still acts on
	// everything the user actually picked, not just what happens to still be visible.
	const selectedMessages = (messages.data ?? []).filter((message) =>
		selectedIds.has(message.id),
	);

	const table = useReactTable({
		data: messages.data ?? [],
		columns,
		state: { sorting, globalFilter: filterText },
		onSortingChange: setSorting,
		onGlobalFilterChange: setFilterText,
		globalFilterFn,
		getCoreRowModel: getCoreRowModel(),
		getSortedRowModel: getSortedRowModel(),
		getFilteredRowModel: getFilteredRowModel(),
	});
	const rows = table.getRowModel().rows;

	const parentRef = useRef<HTMLDivElement>(null);
	const virtualizer = useVirtualizer({
		count: rows.length,
		getScrollElement: () => parentRef.current,
		estimateSize: () => rowHeightEstimate,
		overscan: 8,
	});

	// Follows the current selection, not just the context-menu target, so a shortcut and a
	// multi-select bulk action cannot diverge in what "the selection" means (§13 Epic 6).
	// Every action goes through the mutation queue, so a shortcut and its menu entry cannot
	// diverge in what they actually do either (§13).
	useShortcuts([
		{
			key: "u",
			description: "Mark unread",
			run: () =>
				selectedMessages.length > 0 &&
				setFlags.mutate({
					messages: selectedMessages,
					isRead: false,
					isFlagged: null,
				}),
		},
		{
			key: "i",
			description: "Mark read",
			run: () =>
				selectedMessages.length > 0 &&
				setFlags.mutate({
					messages: selectedMessages,
					isRead: true,
					isFlagged: null,
				}),
		},
		{
			key: "s",
			description: "Flag",
			run: () => {
				if (selectedMessages.length === 0) return;
				// Mixed selection: flagging wins over unflagging, the same "act, don't ask"
				// default a mixed-read selection's context-menu label uses below.
				const allFlagged = selectedMessages.every(
					(message) => message.isFlagged,
				);
				setFlags.mutate({
					messages: selectedMessages,
					isRead: null,
					isFlagged: !allFlagged,
				});
			},
		},
		{
			key: "Delete",
			description: "Move to trash",
			run: () => selectedMessages.length > 0 && trash.mutate(selectedMessages),
		},
	]);

	if (messages.isPending) return <SkeletonText paragraph lineCount={6} />;
	if (messages.isError)
		return <p className={styles.empty}>Could not load messages.</p>;
	if (messages.data.length === 0)
		return (
			<p className={styles.empty}>
				{searching ? "No messages match that search." : "Nothing here yet."}
			</p>
		);

	return (
		<>
			<div className={styles.toolbar}>
				<TextInput
					id="message-list-filter"
					labelText="Filter messages"
					hideLabel
					placeholder="Filter messages…"
					size="sm"
					value={filterText}
					onChange={(event) => setFilterText(event.target.value)}
				/>
			</div>
			<div className={styles.headerRow} role="row">
				{table.getHeaderGroups()[0].headers.map((header) => {
					const sorted = header.column.getIsSorted();
					return (
						<button
							key={header.id}
							type="button"
							className={styles.headerCell}
							onClick={header.column.getToggleSortingHandler()}
							aria-sort={
								sorted === "asc"
									? "ascending"
									: sorted === "desc"
										? "descending"
										: "none"
							}
						>
							{flexRender(header.column.columnDef.header, header.getContext())}
							{sorted === "asc" ? " ▲" : sorted === "desc" ? " ▼" : null}
						</button>
					);
				})}
				<span className={styles.headerCell} aria-hidden="true" />
			</div>
			{rows.length === 0 ? (
				<p className={styles.empty}>No messages match that filter.</p>
			) : (
				<div ref={parentRef} className={styles.scroller} role="list">
					<div
						className={styles.spacer}
						style={{ height: virtualizer.getTotalSize() }}
					>
						{virtualizer.getVirtualItems().map((item) => {
							const message = rows[item.index].original;
							const index = item.index;
							const read = isRead(message, pending.data);
							const isSelected = selectedIds.has(message.id);
							return (
								<div
									key={message.id}
									ref={virtualizer.measureElement}
									data-index={index}
									role="listitem"
									className={styles.virtualRow}
									style={{ transform: `translateY(${item.start}px)` }}
								>
									<button
										type="button"
										aria-pressed={isSelected}
										className={`${styles.row} ${read ? "" : styles.unread} ${isSelected ? styles.selected : ""}`}
										draggable
										onDragStart={(event) => {
											// Dragging a row that's part of a multi-selection carries
											// the whole selection; dragging any other row carries just
											// itself (§13 Epic 6), matching the same "acts on the whole
											// selection, or resets to one" convention right-click uses.
											const ids =
												isSelected && selectedIds.size > 1
													? [...selectedIds]
													: [message.id];
											event.dataTransfer.setData(
												messageDragType,
												ids.join(","),
											);
											event.dataTransfer.effectAllowed = "move";
										}}
										onContextMenu={(event) => {
											event.preventDefault();
											// Right-clicking a message already part of a
											// multi-selection acts on the whole selection, following
											// the same convention as ctrl/shift-click; right-clicking
											// outside it starts a new one-message selection instead of
											// leaving a stale one active.
											const targets =
												isSelected && selectedIds.size > 1
													? selectedMessages
													: [message];
											if (!isSelected || selectedIds.size === 1) {
												setSelectedIds(new Set([message.id]));
												setAnchorIndex(index);
											}
											setMenu({ x: event.clientX, y: event.clientY, targets });
										}}
										onClick={(event) => {
											if (event.shiftKey && anchorIndex !== null) {
												const [start, end] = [
													Math.min(anchorIndex, index),
													Math.max(anchorIndex, index),
												];
												setSelectedIds(
													new Set(
														rows
															.slice(start, end + 1)
															.map((row) => row.original.id),
													),
												);
												return;
											}

											if (event.ctrlKey || event.metaKey) {
												setSelectedIds((current) => {
													const next = new Set(current);
													if (next.has(message.id)) next.delete(message.id);
													else next.add(message.id);
													return next;
												});
												setAnchorIndex(index);
												return;
											}

											setSelectedIds(new Set([message.id]));
											setAnchorIndex(index);
											onSelect({ ...message, from: senderAddress(message) });
											// Opening a message marks it read, as every mail client
											// does. Already-read messages enqueue nothing: a
											// redundant mutation would still be a real provider call.
											if (!read)
												setFlags.mutate({
													messages: [message],
													isRead: true,
													isFlagged: null,
												});
										}}
									>
										<span className={styles.cell}>
											{describeSender(message)}
										</span>
										<span className={styles.cell}>
											{message.subject || "(no subject)"}
											<br />
											<span className={styles.sender}>{message.snippet}</span>
										</span>
										<span className={styles.cell}>
											{new Date(message.receivedAt).toLocaleString()}
										</span>
										<span className={styles.indicators} aria-hidden="true">
											{message.isFlagged ? "🚩" : null}
											{message.hasNonInlineAttachments ? "📎" : null}
										</span>
									</button>
								</div>
							);
						})}
					</div>
				</div>
			)}
			{menu ? (
				<MessageContextMenu
					open
					x={menu.x}
					y={menu.y}
					onClose={() => setMenu(null)}
					actions={messageActions(
						menu.targets,
						setFlags.mutate,
						trash.mutate,
						deletePermanently.mutate,
						hub,
						queryClient,
						onPrint,
					)}
				/>
			) : null}
		</>
	);
}

/**
 * The conventional message menu.
 *
 * Entries whose feature does not exist yet are present and disabled, with the reason: §13 asks
 * for the conventional menu per item type, and a menu that grows entries as features land
 * reads as an app that keeps changing shape.
 */
function messageActions(
	targets: MessageSummary[],
	setFlags: (input: {
		messages: MessageSummary[];
		isRead: boolean | null;
		isFlagged: boolean | null;
	}) => void,
	trash: (messages: MessageSummary[]) => void,
	deletePermanently: (messages: MessageSummary[]) => void,
	hub: HubConnection,
	queryClient: QueryClient,
	onPrint: (message: { id: string; subject: string; from: string }) => void,
): MenuAction[] {
	const single = targets.length === 1 ? targets[0] : undefined;
	const suffix = targets.length > 1 ? ` (${targets.length})` : "";
	// A mixed selection acts rather than asks: marking everything read/flagged is the
	// least-surprising outcome for "some of these already are," the same convention email
	// clients use for a mixed toolbar state.
	const allRead = targets.every((message) => message.isRead);
	const allFlagged = targets.every((message) => message.isFlagged);
	const singleUnavailable =
		targets.length > 1
			? "Only available for one message at a time."
			: undefined;

	return [
		{
			label: "Reply",
			run: () => undefined,
			unavailable: "Compose is not built yet.",
		},
		{
			label: "Reply all",
			run: () => undefined,
			unavailable: "Compose is not built yet.",
		},
		{
			label: "Forward",
			run: () => undefined,
			unavailable: "Compose is not built yet.",
		},
		{ label: "-", run: () => undefined },
		{
			label: `${allRead ? "Mark unread" : "Mark read"}${suffix}`,
			run: () =>
				setFlags({ messages: targets, isRead: !allRead, isFlagged: null }),
		},
		{
			label: `${allFlagged ? "Remove flag" : "Flag"}${suffix}`,
			run: () =>
				setFlags({ messages: targets, isRead: null, isFlagged: !allFlagged }),
		},
		{ label: "-", run: () => undefined },
		{
			label: `Move to trash${suffix}`,
			run: () => trash(targets),
			danger: true,
		},
		{
			label: `Delete permanently${suffix}`,
			run: () => deletePermanently(targets),
			danger: true,
		},
		{
			label: "Save as .eml",
			run: () => single && void saveAsEml(hub, single),
			unavailable: singleUnavailable,
		},
		{
			label: "Print",
			run: () => single && void printMessage(hub, queryClient, single, onPrint),
			unavailable: singleUnavailable,
		},
	];
}

/**
 * Loads the body into the query cache before switching to the reading pane, so it renders
 * already-fetched rather than showing "Downloading this message…" under the print dialog —
 * printing has to go through that same sandboxed render, never a second ad-hoc one (§13).
 */
async function printMessage(
	hub: HubConnection,
	queryClient: QueryClient,
	message: MessageSummary,
	onPrint: (message: { id: string; subject: string; from: string }) => void,
): Promise<void> {
	await queryClient.fetchQuery({
		queryKey: ["body", message.id],
		queryFn: () => hub.invoke("GetMessageBody", message.id),
	});
	onPrint({ ...message, from: senderAddress(message) });
	// One frame so the reading pane has actually mounted the now-cached body before printing.
	requestAnimationFrame(() => window.print());
}

/**
 * Hands the raw MIME to the OS's own save flow rather than opening a bespoke dialog: an
 * anchor with a `blob:` URL and `download` set is what a browser's download manager — which
 * Electron's `BrowserWindow` already runs — is for (§13 Export).
 */
async function saveAsEml(
	hub: HubConnection,
	message: MessageSummary,
): Promise<void> {
	const base64 = await hub.invoke<string>("SaveMessageAsEml", message.id);
	const bytes = Uint8Array.from(atob(base64), (char) => char.charCodeAt(0));
	const blob = new Blob([bytes], { type: "message/rfc822" });
	const url = URL.createObjectURL(blob);
	try {
		const link = document.createElement("a");
		link.href = url;
		link.download = `${message.subject || "message"}.eml`;
		link.click();
	} finally {
		URL.revokeObjectURL(url);
	}
}

/** Desired state wins over server-known state while a mutation is outstanding (§6). */
function isRead(
	message: MessageSummary,
	pending: PendingChange[] | undefined,
): boolean {
	const desired = pending?.find(
		(change) => change.messageId === message.id && change.field === isReadField,
	);
	return desired?.desiredValue ?? message.isRead;
}

function describeSender(message: MessageSummary): string {
	const [first] = message.from;
	if (!first) return "(unknown sender)";
	return first.name ?? first.email;
}

/** The address the remote-content allow list keys on — never the display name. */
function senderAddress(message: MessageSummary): string {
	return message.from[0]?.email ?? "";
}
