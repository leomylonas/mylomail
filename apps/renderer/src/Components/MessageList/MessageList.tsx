import { useEffect, useRef, useState } from "react";
import {
	useInfiniteQuery,
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
	type LegacyFeatures,
	type LegacyColumnDef,
} from "@tanstack/react-table/legacy";
import { useVirtualizer } from "@tanstack/react-virtual";
import type { HubConnection } from "@microsoft/signalr";
import { Button, SkeletonText, TextInput } from "@carbon/react";
import { queryKeys } from "@mylomail/renderer/Shell/Backend/HubConnection";
import { MessageContextMenu } from "@mylomail/renderer/Shell/Registries/ContextMenus/MessageContextMenu/MessageContextMenu";
import type { MenuAction } from "@mylomail/renderer/Shell/Registries/ContextMenus/ContextMenus";
import { useShortcuts } from "@mylomail/renderer/Shell/Registries/Shortcuts/UseShortcuts";
import { messageDragType } from "@mylomail/renderer/Lib/DragTypes";
import {
	buildForwardSeed,
	buildReplySeed,
	resolveOriginalHtml,
	type ComposeSeed,
	type ForwardAttachment,
	type MessageReplyContext,
} from "@mylomail/renderer/Components/Compose/ComposeReplyForward";
import { useWindowNotifications } from "@mylomail/renderer/Shell/Registries/Notifications/UseNotifications";
import { notify } from "@mylomail/renderer/Shell/Registries/Notifications/NotificationStore";
import { present } from "@mylomail/renderer/Shell/Registries/Errors/ErrorPresentation";
import type { ErrorCategory } from "@mylomail/shared-types/SignalR/MyloMail.Api.Errors";
import { parseSearchSnippet } from "@mylomail/renderer/Components/MessageList/SearchSnippet";
import {
	isRovingFocusKey,
	nextFocusIndex,
} from "@mylomail/renderer/Lib/RovingFocus";
import styles from "@mylomail/renderer/Components/MessageList/MessageList.module.css";

export interface MessageSummary {
	id: string;
	subject: string;
	snippet: string;
	from: { name: string | null; email: string }[];
	receivedAt: string;
	isRead: boolean;
	isFlagged: boolean;
	hasNonInlineAttachments: boolean;
	/**
	 * The category of the message's most recent mutation, if it ended terminally failed or
	 * cancelled (§13 Epic 4) — never stale, since it always reflects the highest-sequence
	 * mutation, not "ever failed." Absent once a later mutation succeeds.
	 */
	mutationFailure: ErrorCategory | null;
	/**
	 * A match-context excerpt from FTS5's own `snippet()`, present only on search results
	 * (§8) — absent for an ordinary mailbox listing, which never ran a query to excerpt.
	 */
	searchSnippet?: string;
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

/** Messages fetched per `GetMessages` page (§12) — also the signal `hasNextPage` uses: a
 * page shorter than this is the last one. */
const messagePageSize = 100;

/**
 * Matches across the fields the row itself displays, not full-text search: this is a fast,
 * local narrowing of whatever page of messages is already loaded, distinct from the `Search`
 * hub method the search box already triggers server-side (§12, §13 Epic 6).
 */
const globalFilterFn: FilterFn<LegacyFeatures, MessageSummary> = (
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

// TanStack Table's `ColumnDef` is invariant enough in its value type parameter that an array
// mixing per-column accessor value types (string here, in every case, but inferred separately
// per column) cannot be given one precise array-level type without fighting the type checker —
// a well-known rough edge of this API, not a sign something here is actually wrong. Cast once,
// at the single point `useReactTable` consumes it, rather than widening every column to `any`.
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
		sortFn: (a, b) =>
			new Date(a.original.receivedAt).getTime() -
			new Date(b.original.receivedAt).getTime(),
	}),
];

export function MessageList({
	hub,
	accountId,
	ownAddress,
	mailboxId,
	query,
	onSelect,
	onPrint,
	onCompose,
}: {
	hub: HubConnection;
	accountId: string;
	/** This account's own address, so reply-all can exclude replying to yourself (§13). */
	ownAddress: string;
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
	/** Opens compose prefilled as a reply/reply-all/forward (§13). */
	onCompose: (seed: ComposeSeed) => void;
}) {
	const queryClient = useQueryClient();
	const { store: notifications } = useWindowNotifications();
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

	// Search never paginates: it is a bounded, already-ranked result set from FTS5, not a
	// mailbox listing a user might scroll through thousands of. Searching scopes to the
	// selected mailbox, because a search from inside a folder that silently returned results
	// from everywhere would be a different question than the one the user asked.
	const search = useQuery({
		queryKey: queryKeys.search(accountId, query, mailboxId),
		queryFn: () =>
			hub.invoke<MessageSummary[]>("Search", accountId, query, mailboxId),
		enabled: searching,
	});

	// Ordinary mailbox listing pages by `skip`/`take` (§12): a mailbox can hold far more than
	// one page's worth of messages, and loading them all up front would mean a slow initial
	// render and an ever-growing payload for every account, not just large ones.
	const listing = useInfiniteQuery({
		queryKey: queryKeys.messages(mailboxId),
		queryFn: ({ pageParam }) =>
			hub.invoke<MessageSummary[]>(
				"GetMessages",
				mailboxId,
				pageParam,
				messagePageSize,
			),
		initialPageParam: 0,
		getNextPageParam: (lastPage, pages) =>
			lastPage.length < messagePageSize
				? undefined
				: pages.length * messagePageSize,
		enabled: !searching,
	});

	const messages = searching
		? search
		: {
				data: listing.data?.pages.flat() ?? [],
				isPending: listing.isPending,
				isError: listing.isError,
			};

	// What the user has asked for and the server has not yet confirmed. Merged over
	// server-known state so a flag they just toggled does not flicker back while its mutation
	// is in flight (§6).
	const pending = useQuery({
		queryKey: queryKeys.pending(accountId),
		queryFn: () =>
			hub.invoke<PendingChange[]>("GetPendingSyncState", accountId),
	});

	// The enqueue itself failing (hub disconnected, validation) is not the same as a later
	// provider-side failure, which the global MessageSyncFailed handler already surfaces —
	// without this, a failure of the hub.invoke() call itself failed with no explanation.
	const reportFailure = (title: string) => (error: unknown) =>
		notify(notifications, {
			kind: "error",
			title,
			detail: error instanceof Error ? error.message : String(error),
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
		onError: reportFailure("The flag could not be changed"),
	});

	const trash = useMutation({
		mutationFn: (messages: MessageSummary[]) =>
			hub.invoke(
				"MoveToTrash",
				accountId,
				messages.map((message) => message.id),
			),
		onSettled: () => queryClient.invalidateQueries({ queryKey: ["messages"] }),
		onError: reportFailure("The message could not be moved to trash"),
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
		onError: reportFailure("The message could not be deleted"),
	});

	// Selection tracks the full, unfiltered list: a message shift/ctrl-selected before a local
	// filter narrowed the view stays selected, so a bulk action or shortcut still acts on
	// everything the user actually picked, not just what happens to still be visible.
	const selectedMessages = (messages.data ?? []).filter((message) =>
		selectedIds.has(message.id),
	);

	const table = useReactTable({
		data: messages.data ?? [],
		columns: columns as LegacyColumnDef<MessageSummary>[],
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

	// A scroll offset from the previous mailbox (or search) means nothing against a completely
	// different result set — left alone, switching mailboxes deep in a long list leaves the view
	// scrolled to an arbitrary point in the new one, showing unrelated rows or a blank overscroll
	// area while its own first page is still loading in at the top.
	//
	// The same is true of a roving-tabindex target left over from the previous mailbox/search —
	// index N in the old result set means nothing in the new one, so it's cleared here too
	// rather than silently pointing at an unrelated row.
	useEffect(() => {
		virtualizer.scrollToOffset(0);
		setFocusedIndex(null);
	}, [mailboxId, searching, virtualizer]);

	// Roving tabindex (§13): a virtualized list of native `<button>` rows has no built-in
	// keyboard navigation between them — Tab only ever lands on whatever the browser happens to
	// have rendered, and a row scrolled out of the virtualized viewport is unreachable without a
	// mouse to scroll first. `focusedIndex` is the one row Tab can land on; every other row gets
	// `tabIndex={-1}` so arrow keys move a single roving focus point instead.
	const [focusedIndex, setFocusedIndex] = useState<number | null>(null);
	const rowRefs = useRef<Map<number, HTMLButtonElement>>(new Map());
	// Invalidates in-flight focusRowWhenReady retry chains from an earlier key press: without
	// this, holding an arrow key down spawns one independent rAF chain per keystroke, and
	// whichever happens to resolve last "wins" the DOM focus call regardless of which key press
	// it actually came from.
	const focusRequestId = useRef(0);

	// Falls back sensibly when the row last given keyboard focus no longer exists at that index
	// — a concurrent mutation (this window's own action, or a sync-driven remove/re-sort) can
	// shrink or reorder the list between one render and the next. Preferring the selected row,
	// then the first row, over losing focus into the void entirely — but only among rows the
	// virtualizer has actually mounted: an index outside that set would leave no DOM element
	// with tabIndex=0 at all, making the whole list untabbable-into until the next scroll.
	const renderedIndices = virtualizer
		.getVirtualItems()
		.map((item) => item.index);
	const tabbableIndex =
		renderedIndices.length === 0
			? null
			: (() => {
					const selectedIndex = rows.findIndex((row) =>
						selectedIds.has(row.original.id),
					);
					const preferred = [focusedIndex, selectedIndex, renderedIndices[0]];
					return (
						preferred.find((index) => renderedIndices.includes(index ?? -1)) ??
						renderedIndices[0]
					);
				})();

	function focusRowWhenReady(
		index: number,
		requestId: number,
		attemptsRemaining = 5,
	): void {
		if (focusRequestId.current !== requestId) return;
		const element = rowRefs.current.get(index);
		if (element) {
			element.focus();
			return;
		}
		// The virtualizer's scroll-triggered re-render hasn't mounted this row's DOM node yet —
		// bounded retries across frames rather than an effect dependency, since there is no
		// single prop that reliably changes exactly when react-virtual finishes that render.
		if (attemptsRemaining > 0) {
			requestAnimationFrame(() =>
				focusRowWhenReady(index, requestId, attemptsRemaining - 1),
			);
		}
	}

	function moveRovingFocus(
		key: "ArrowUp" | "ArrowDown" | "Home" | "End",
		currentIndex: number,
	): void {
		const next = nextFocusIndex(key, currentIndex, rows.length);
		if (next === currentIndex) return;
		setFocusedIndex(next);
		virtualizer.scrollToIndex(next, { align: "auto" });
		focusRequestId.current += 1;
		focusRowWhenReady(next, focusRequestId.current);
	}

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
			{/*
				This is a selectable list of composite rows (whole-row click/select, à la a
				listbox), not a cell-navigable data grid — `row`/`columnheader` roles require a
				`table`/`grid` ancestor to be exposed correctly by assistive tech, which nothing
				here provides or is structured for (each row is one `<button>`, not per-column
				cells). A plain labelled group of sort toggle buttons matches what this actually
				is, and stays consistent with the body's own `list`/`listitem` roles below.
			*/}
			<div
				className={styles.headerRow}
				role="group"
				aria-label="Sort messages by"
			>
				{table.getHeaderGroups()[0].headers.map((header) => {
					const sorted = header.column.getIsSorted();
					const label = flexRender(
						header.column.columnDef.header,
						header.getContext(),
					);
					return (
						<button
							key={header.id}
							type="button"
							className={styles.headerCell}
							onClick={header.column.getToggleSortingHandler()}
							aria-pressed={sorted !== false}
							aria-label={`Sort by ${String(label)}${
								sorted === "asc"
									? ", ascending"
									: sorted === "desc"
										? ", descending"
										: ""
							}`}
						>
							{label}
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
										ref={(element) => {
											if (element) rowRefs.current.set(index, element);
											else rowRefs.current.delete(index);
										}}
										tabIndex={index === tabbableIndex ? 0 : -1}
										aria-pressed={isSelected}
										className={`${styles.row} ${read ? "" : styles.unread} ${isSelected ? styles.selected : ""}`}
										draggable
										onKeyDown={(event) => {
											if (!isRovingFocusKey(event.key)) return;
											event.preventDefault();
											moveRovingFocus(event.key, index);
										}}
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
											setFocusedIndex(index);
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
											<span className={styles.sender}>
												{message.searchSnippet
													? parseSearchSnippet(message.searchSnippet).map(
															(segment, index) =>
																segment.highlighted ? (
																	<mark key={index}>{segment.text}</mark>
																) : (
																	<span key={index}>{segment.text}</span>
																),
														)
													: message.snippet}
											</span>
										</span>
										<span className={styles.cell}>
											{new Date(message.receivedAt).toLocaleString()}
										</span>
										<span className={styles.indicators}>
											{message.isFlagged ? (
												<span aria-hidden="true">🚩</span>
											) : null}
											{message.hasNonInlineAttachments ? (
												<span aria-hidden="true">📎</span>
											) : null}
											{message.mutationFailure !== null ? (
												<span
													role="img"
													aria-label={
														present(message.mutationFailure, null).title
													}
													title={present(message.mutationFailure, null).title}
												>
													⚠️
												</span>
											) : null}
										</span>
									</button>
								</div>
							);
						})}
					</div>
				</div>
			)}
			{!searching && listing.hasNextPage ? (
				<div className={styles.loadMore}>
					<Button
						kind="ghost"
						size="sm"
						disabled={listing.isFetchingNextPage}
						onClick={() => void listing.fetchNextPage()}
					>
						{listing.isFetchingNextPage ? "Loading…" : "Load more"}
					</Button>
					{listing.isFetchNextPageError ? (
						<p className={styles.empty} role="alert">
							Could not load more messages.
						</p>
					) : null}
				</div>
			) : null}
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
						onCompose,
						ownAddress,
						reportFailure,
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
export function messageActions(
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
	onCompose: (seed: ComposeSeed) => void,
	ownAddress: string,
	reportFailure: (title: string) => (error: unknown) => void,
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
			run: () =>
				single && void replyTo(hub, single, "reply", ownAddress, onCompose),
			unavailable: singleUnavailable,
		},
		{
			label: "Reply all",
			run: () =>
				single && void replyTo(hub, single, "replyAll", ownAddress, onCompose),
			unavailable: singleUnavailable,
		},
		{
			label: "Forward",
			run: () => single && void forward(hub, single, onCompose),
			unavailable: singleUnavailable,
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
			// Unlike every other destructive action here (trash, mailbox delete, account
			// removal, a recurring series' whole-series delete), this had no confirmation at
			// all — a single misclick on a danger-styled menu item did something the app itself
			// documents as never reversible.
			run: () => {
				if (
					window.confirm(
						targets.length > 1
							? `Permanently delete ${targets.length} messages? This cannot be undone.`
							: "Permanently delete this message? This cannot be undone.",
					)
				) {
					deletePermanently(targets);
				}
			},
			danger: true,
		},
		{
			label: "Save as .eml",
			run: () =>
				single &&
				void saveAsEml(hub, single).catch(
					reportFailure("The message could not be saved"),
				),
			unavailable: singleUnavailable,
		},
		{
			label: "Print",
			run: () =>
				single &&
				void printMessage(hub, queryClient, single, onPrint).catch(
					reportFailure("The message could not be printed"),
				),
			unavailable: singleUnavailable,
		},
	];
}

/**
 * Fetches the address/subject/date context and body a reply needs, then opens compose seeded
 * from it. Fetched fresh on every click rather than reused from the row's own summary data:
 * `MessageSummary` carries only `from`, not the `to`/`cc`/reply-to a reply actually needs (§13).
 */
async function replyTo(
	hub: HubConnection,
	message: MessageSummary,
	mode: "reply" | "replyAll",
	ownAddress: string,
	onCompose: (seed: ComposeSeed) => void,
): Promise<void> {
	const [context, body] = await Promise.all([
		hub.invoke<MessageReplyContext>("GetMessageReplyContext", message.id),
		hub.invoke<{
			html: string | null;
			text: string | null;
			isFetched: boolean;
			isFailed: boolean;
		}>("GetMessageBody", message.id),
	]);
	onCompose(
		buildReplySeed(mode, context, resolveOriginalHtml(body), ownAddress),
	);
}

/**
 * As {@link replyTo}, plus the original message's own non-inline attachments — copied onto
 * the new draft by `Compose` itself once it exists, not fetched here (§13).
 */
async function forward(
	hub: HubConnection,
	message: MessageSummary,
	onCompose: (seed: ComposeSeed) => void,
): Promise<void> {
	const [context, body, attachments] = await Promise.all([
		hub.invoke<MessageReplyContext>("GetMessageReplyContext", message.id),
		hub.invoke<{
			html: string | null;
			text: string | null;
			isFetched: boolean;
			isFailed: boolean;
		}>("GetMessageBody", message.id),
		hub.invoke<ForwardAttachment[]>("GetAttachmentMetadata", message.id),
	]);
	onCompose(buildForwardSeed(context, resolveOriginalHtml(body), attachments));
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
