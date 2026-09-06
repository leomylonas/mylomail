import { useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import type { HubConnection } from "@microsoft/signalr";
import {
	Modal,
	NumberInput,
	RadioButton,
	RadioButtonGroup,
	SkeletonText,
} from "@carbon/react";
import {
	queryKeys,
	type SyncProgress,
} from "@mylomail/renderer/Shell/Backend/HubConnection";
import {
	CoverageStatus,
	InitialSyncMode,
	SpecialUse,
} from "@mylomail/shared-types/SignalR/MyloMail.Api.Domain";
import { useWindowStore } from "@mylomail/renderer/Shell/WindowScope/WindowScope";
import { useStoreValue } from "@mylomail/renderer/Shell/WindowScope/UseStoreValue";
import { MessageContextMenu } from "@mylomail/renderer/Shell/Registries/ContextMenus/MessageContextMenu/MessageContextMenu";
import { FolderNameModal } from "@mylomail/renderer/Components/MailboxTree/FolderNameModal/FolderNameModal";
import { useWindowNotifications } from "@mylomail/renderer/Shell/Registries/Notifications/UseNotifications";
import { notify } from "@mylomail/renderer/Shell/Registries/Notifications/NotificationStore";
import {
	mailboxDragType,
	messageDragType,
} from "@mylomail/renderer/Lib/DragTypes";
import styles from "@mylomail/renderer/Components/MailboxTree/MailboxTree.module.css";

export interface Mailbox {
	id: string;
	parentId: string | null;
	name: string;
	providerTotalCount: number | null;
	providerUnreadCount: number | null;
	localCount: number;
	isCollapsed: boolean;
	coverage: CoverageStatus;
	initialSyncModeOverride: InitialSyncMode | null;
	initialSyncBoundValueOverride: number | null;
	/**
	 * No real provider object backs this row — a Gmail nested-label intermediate the sidebar
	 * derived by splitting a label name on `/` (§1). Renaming or deleting it has nothing to
	 * act on, so neither is offered (§13 Epic 2).
	 */
	isSynthesized: boolean;
	/** The server-reported attribute, or the provider's own name-based fallback guess for a
	 * server that didn't advertise one — before any user correction below is applied. */
	specialUse: SpecialUse;
	/** A user correction of {@link specialUse} (§13 Epic 2), for when the automatic guess is
	 * wrong or the server names things this fallback doesn't recognise. */
	specialUseOverride: SpecialUse | null;
}

interface Capabilities {
	deletingMailboxDeletesMessages: boolean;
}

type Dialog =
	| { kind: "create"; parent: Mailbox | null }
	| { kind: "rename"; mailbox: Mailbox }
	| { kind: "delete"; mailbox: Mailbox }
	| { kind: "sync"; mailbox: Mailbox }
	| { kind: "special-use"; mailbox: Mailbox };

export function MailboxTree({
	hub,
	accountId,
}: {
	hub: HubConnection;
	accountId: string;
}) {
	const store = useWindowStore();
	const queryClient = useQueryClient();
	const { store: notifications } = useWindowNotifications();
	const selectedMailboxId = useStoreValue(store, "selectedMailboxId");
	const [menu, setMenu] = useState<{
		x: number;
		y: number;
		mailbox: Mailbox;
	} | null>(null);
	const [dialog, setDialog] = useState<Dialog | null>(null);
	const [dropTarget, setDropTarget] = useState<string | null>(null);

	const close = () => setDialog(null);
	const refresh = () => {
		close();
		return queryClient.invalidateQueries({
			queryKey: queryKeys.mailboxes(accountId),
		});
	};

	// Folder operations are the one part of the sidebar that can fail visibly to the user:
	// the provider rejects a duplicate name, a namespace it will not accept, or a delete of a
	// special folder. Without this the dialog simply stayed open and said nothing, which is
	// indistinguishable from the app having ignored the click.
	const reportFailure = (title: string) => (error: unknown) =>
		notify(notifications, {
			kind: "error",
			title,
			detail: error instanceof Error ? error.message : String(error),
		});

	const create = useMutation({
		mutationFn: ({
			name,
			parentId,
		}: {
			name: string;
			parentId: string | null;
		}) => hub.invoke("CreateMailbox", accountId, name, parentId),
		onSuccess: refresh,
		onError: reportFailure("The folder could not be created"),
	});

	const rename = useMutation({
		mutationFn: ({ id, name }: { id: string; name: string }) =>
			hub.invoke("RenameMailbox", id, name),
		onSuccess: refresh,
		onError: reportFailure("The folder could not be renamed"),
	});

	// Drag-a-message-onto-a-folder and folder drag-reorder (§13 Epic 2). Native HTML5 DnD: no
	// library pulls its own weight for two drop targets, and Carbon's buttons already forward
	// arbitrary DOM props like `draggable`/`onDragStart`/`onDrop`.
	const moveMessages = useMutation({
		mutationFn: (input: { messageIds: string[]; targetMailboxId: string }) =>
			hub.invoke(
				"MoveMessages",
				accountId,
				input.messageIds,
				input.targetMailboxId,
			),
		onSuccess: () => {
			void queryClient.invalidateQueries({ queryKey: ["messages"] });
			void queryClient.invalidateQueries({
				queryKey: queryKeys.pending(accountId),
			});
		},
		onError: reportFailure("The message could not be moved"),
	});

	const moveMailbox = useMutation({
		mutationFn: (input: { mailboxId: string; newParentId: string | null }) =>
			hub.invoke("MoveMailbox", input.mailboxId, input.newParentId),
		onSuccess: refresh,
		onError: reportFailure("The folder could not be moved"),
	});

	const reorderMailboxes = useMutation({
		mutationFn: (input: {
			parentId: string | null;
			orderedMailboxIds: string[];
		}) =>
			hub.invoke(
				"ReorderMailboxes",
				accountId,
				input.parentId,
				input.orderedMailboxIds,
			),
		onSuccess: refresh,
		onError: reportFailure("The folders could not be reordered"),
	});

	// Server-persisted, not component state (§13 Epic 2: "expand/collapse state persists...
	// across restarts") — a query refetch is what shows the toggle rather than an optimistic
	// local flag, the same choice Sidebar's account-level toggle makes for the same reason.
	const toggleCollapsed = useMutation({
		mutationFn: (input: { mailboxId: string; collapsed: boolean }) =>
			hub.invoke("SetMailboxCollapsed", input.mailboxId, input.collapsed),
		onSuccess: refresh,
	});

	const setSyncOverride = useMutation({
		mutationFn: (input: {
			mailboxId: string;
			mode: InitialSyncMode | null;
			boundValue: number | null;
		}) =>
			hub.invoke(
				"SetMailboxInitialSyncOverride",
				input.mailboxId,
				input.mode,
				input.boundValue,
			),
		onSuccess: refresh,
		onError: reportFailure("The sync setting could not be saved"),
	});

	const setSpecialUseOverride = useMutation({
		mutationFn: (input: { mailboxId: string; specialUse: SpecialUse | null }) =>
			hub.invoke(
				"SetMailboxSpecialUseOverride",
				input.mailboxId,
				input.specialUse,
			),
		onSuccess: refresh,
		onError: reportFailure("The folder role could not be saved"),
	});

	const remove = useMutation({
		mutationFn: (mailbox: Mailbox) =>
			hub.invoke<boolean>("DeleteMailbox", mailbox.id),
		onSuccess: async (messagesWentToo, mailbox) => {
			// Reported after the fact as well as warned about before it, because the provider
			// is the one that decides and the answer is not the same on every account.
			notify(notifications, {
				kind: "success",
				title: `Deleted "${mailbox.name}"`,
				detail: messagesWentToo
					? "Its messages were deleted with it."
					: "Its messages are still in All Mail.",
			});

			if (mailbox.id === selectedMailboxId) {
				store.setState("selectedMailboxId", null);
			}

			await refresh();
		},
		onError: reportFailure("The folder could not be deleted"),
	});

	const mailboxes = useQuery({
		queryKey: queryKeys.mailboxes(accountId),
		queryFn: () => hub.invoke<Mailbox[]>("GetMailboxes", accountId),
	});

	// Fetched alongside the tree rather than when the confirmation opens: a dialog that has to
	// wait for a round trip before it can say what deleting does would either flash the wrong
	// wording or block on the network at the moment the user is deciding.
	const capabilities = useQuery({
		queryKey: queryKeys.accountCapabilities(accountId),
		queryFn: () =>
			hub.invoke<Capabilities>("GetAccountCapabilities", accountId),
	});

	if (mailboxes.isPending) return <SkeletonText paragraph lineCount={5} />;
	if (mailboxes.isError) return <p>Could not load mailboxes.</p>;

	const children = (parentId: string | null) =>
		mailboxes.data.filter((mailbox) => (mailbox.parentId ?? null) === parentId);

	const dropOnMailbox = (event: React.DragEvent, target: Mailbox) => {
		event.preventDefault();
		setDropTarget(null);

		const messageIds = event.dataTransfer.getData(messageDragType);
		if (messageIds) {
			// A synthesized intermediate has no real label to add — the same reason
			// Rename/Delete are unavailable on it (§13 Epic 2). Caught here, before the
			// mutation, rather than left to fail with a raw provider error.
			if (target.isSynthesized) {
				notify(notifications, {
					kind: "error",
					title: "Can't move a message here",
					detail:
						"This is a nested label group, not a real Gmail label — move the message into one of the labels inside it instead.",
				});
				return;
			}
			moveMessages.mutate({
				messageIds: messageIds.split(","),
				targetMailboxId: target.id,
			});
			return;
		}

		const draggedMailboxId = event.dataTransfer.getData(mailboxDragType);
		if (!draggedMailboxId || draggedMailboxId === target.id) return;

		const dragged = mailboxes.data.find((m) => m.id === draggedMailboxId);
		if (!dragged || isDescendantOf(mailboxes.data, target, draggedMailboxId))
			return;

		// A synthesized intermediate has no real label of its own to move — the same reason
		// Rename/Delete are unavailable on it (§13 Epic 2). A same-parent reorder is still just
		// local LocalSortOrder bookkeeping and never touches the provider, so only a genuine
		// reparent (the moveMailbox branch below) needs this guard.
		if (
			dragged.isSynthesized &&
			(dragged.parentId ?? null) !== (target.parentId ?? null)
		) {
			notify(notifications, {
				kind: "error",
				title: "Can't move this folder",
				detail:
					"This is a nested label group, not a real Gmail label — move the label itself in Gmail instead.",
			});
			return;
		}

		if ((dragged.parentId ?? null) === (target.parentId ?? null)) {
			// Same parent: a reorder, dropped mailbox lands immediately before the target.
			const siblingIds = children(target.parentId ?? null)
				.map((m) => m.id)
				.filter((id) => id !== draggedMailboxId);
			siblingIds.splice(siblingIds.indexOf(target.id), 0, draggedMailboxId);
			reorderMailboxes.mutate({
				parentId: target.parentId ?? null,
				orderedMailboxIds: siblingIds,
			});
		} else {
			moveMailbox.mutate({
				mailboxId: draggedMailboxId,
				newParentId: target.id,
			});
		}
	};

	const renderLevel = (parentId: string | null, depth: number) => (
		<ul>
			{children(parentId).map((mailbox) => {
				const hasChildren = children(mailbox.id).length > 0;
				return (
					<li key={mailbox.id}>
						<div
							className={`${styles.row} ${dropTarget === mailbox.id ? styles.dropTarget : ""}`}
							style={{
								paddingLeft: `calc(var(--cds-spacing-03) * ${depth + 1})`,
							}}
							onDragOver={(event) => {
								event.preventDefault();
								event.dataTransfer.dropEffect = "move";
							}}
							onDragEnter={() => setDropTarget(mailbox.id)}
							onDragLeave={() =>
								setDropTarget((current) =>
									current === mailbox.id ? null : current,
								)
							}
							onDrop={(event) => dropOnMailbox(event, mailbox)}
						>
							{hasChildren ? (
								<button
									type="button"
									className={styles.chevron}
									aria-label={mailbox.isCollapsed ? "Expand" : "Collapse"}
									aria-expanded={!mailbox.isCollapsed}
									onClick={() =>
										toggleCollapsed.mutate({
											mailboxId: mailbox.id,
											collapsed: !mailbox.isCollapsed,
										})
									}
								>
									{mailbox.isCollapsed ? "▸" : "▾"}
								</button>
							) : (
								<span className={styles.chevronSpacer} aria-hidden />
							)}
							<button
								type="button"
								draggable
								className={`${styles.item} ${mailbox.id === selectedMailboxId ? styles.selected : ""}`}
								aria-current={mailbox.id === selectedMailboxId}
								onClick={() => {
									// Every account's tree is visible at once now (§13 Epic 2), so
									// selecting a mailbox has to say which account it belongs to
									// too — there is no longer a separately-chosen "current
									// account" this tree can assume it already is.
									store.setState("selectedAccountId", accountId);
									store.setState("selectedMailboxId", mailbox.id);
								}}
								onContextMenu={(event) => {
									event.preventDefault();
									setMenu({ x: event.clientX, y: event.clientY, mailbox });
								}}
								onDragStart={(event) => {
									event.dataTransfer.setData(mailboxDragType, mailbox.id);
									event.dataTransfer.effectAllowed = "move";
								}}
							>
								<span className={styles.name} title={mailbox.name}>
									{mailbox.name}
								</span>
								{mailbox.coverage === CoverageStatus.Backfilling ? (
									<BackfillProgress mailboxId={mailbox.id} />
								) : (
									<span className={styles.count}>{describeCount(mailbox)}</span>
								)}
							</button>
						</div>
						{hasChildren && !mailbox.isCollapsed
							? renderLevel(mailbox.id, depth + 1)
							: null}
					</li>
				);
			})}
		</ul>
	);

	return (
		<>
			<nav className={styles.tree} aria-label="Mailboxes">
				{renderLevel(null, 0)}
			</nav>
			{menu ? (
				<MessageContextMenu
					open
					x={menu.x}
					y={menu.y}
					onClose={() => setMenu(null)}
					actions={[
						{
							label: "New subfolder",
							run: () => setDialog({ kind: "create", parent: menu.mailbox }),
						},
						{
							label: "New folder",
							run: () => setDialog({ kind: "create", parent: null }),
						},
						{ label: "-", run: () => undefined },
						{
							label: "Rename",
							run: () => setDialog({ kind: "rename", mailbox: menu.mailbox }),
							unavailable: menu.mailbox.isSynthesized
								? "Gmail doesn't support renaming a nested label group directly — rename the label itself in Gmail."
								: undefined,
						},
						{
							label: "Sync settings…",
							run: () => setDialog({ kind: "sync", mailbox: menu.mailbox }),
							unavailable:
								menu.mailbox.coverage === CoverageStatus.NotStarted
									? undefined
									: "Only available before this folder's initial sync has started.",
						},
						{ label: "-", run: () => undefined },
						...mailboxMoveActions(
							mailboxes.data,
							menu.mailbox,
							(orderedMailboxIds) =>
								reorderMailboxes.mutate({
									parentId: menu.mailbox.parentId ?? null,
									orderedMailboxIds,
								}),
							(newParentId) =>
								moveMailbox.mutate({
									mailboxId: menu.mailbox.id,
									newParentId,
								}),
						),
						{
							label: "Delete",
							run: () => setDialog({ kind: "delete", mailbox: menu.mailbox }),
							danger: true,
							unavailable: menu.mailbox.isSynthesized
								? "Gmail doesn't support deleting a nested label group directly — delete the label itself in Gmail."
								: undefined,
						},
						{
							label: "Folder role…",
							run: () =>
								setDialog({ kind: "special-use", mailbox: menu.mailbox }),
							unavailable: menu.mailbox.isSynthesized
								? "A nested label group has no role of its own — set it on the label itself."
								: undefined,
						},
					]}
				/>
			) : null}
			{dialog?.kind === "create" ? (
				<FolderNameModal
					heading={
						dialog.parent ? `New folder in ${dialog.parent.name}` : "New folder"
					}
					label="Folder name"
					primaryLabel="Create"
					onSubmit={(name) =>
						create.mutate({ name, parentId: dialog.parent?.id ?? null })
					}
					onClose={close}
				/>
			) : null}
			{dialog?.kind === "rename" ? (
				<FolderNameModal
					key={dialog.mailbox.id}
					heading={`Rename ${dialog.mailbox.name}`}
					label="Folder name"
					initialName={dialog.mailbox.name}
					primaryLabel="Rename"
					onSubmit={(name) => rename.mutate({ id: dialog.mailbox.id, name })}
					onClose={close}
				/>
			) : null}
			{dialog?.kind === "delete" ? (
				<Modal
					open
					danger
					modalHeading={`Delete "${dialog.mailbox.name}"?`}
					primaryButtonText="Delete"
					secondaryButtonText="Cancel"
					onRequestSubmit={() => remove.mutate(dialog.mailbox)}
					onRequestClose={close}
					onSecondarySubmit={close}
				>
					<p>{describeDeletion(capabilities.data)}</p>
				</Modal>
			) : null}
			{dialog?.kind === "sync" ? (
				<SyncOverrideModal
					mailbox={dialog.mailbox}
					onSubmit={(mode, boundValue) =>
						setSyncOverride.mutate({
							mailboxId: dialog.mailbox.id,
							mode,
							boundValue,
						})
					}
					onClose={close}
				/>
			) : null}
			{dialog?.kind === "special-use" ? (
				<SpecialUseOverrideModal
					mailbox={dialog.mailbox}
					onSubmit={(specialUse) =>
						setSpecialUseOverride.mutate({
							mailboxId: dialog.mailbox.id,
							specialUse,
						})
					}
					onClose={close}
				/>
			) : null}
		</>
	);
}

/**
 * Corrects a mailbox's role by hand (§13 Epic 2) — for a server that didn't advertise RFC
 * 6154 SPECIAL-USE, whose folder name the provider's own fallback guessed wrong or didn't
 * recognise. "Detect automatically" clears the override, reverting to whatever the provider
 * itself reports.
 */
function SpecialUseOverrideModal({
	mailbox,
	onSubmit,
	onClose,
}: {
	mailbox: Mailbox;
	onSubmit: (specialUse: SpecialUse | null) => void;
	onClose: () => void;
}) {
	const [choice, setChoice] = useState<SpecialUse | "automatic">(
		mailbox.specialUseOverride ?? "automatic",
	);

	return (
		<Modal
			open
			modalHeading={`Folder role for "${mailbox.name}"`}
			primaryButtonText="Save"
			secondaryButtonText="Cancel"
			onRequestSubmit={() => onSubmit(choice === "automatic" ? null : choice)}
			onRequestClose={onClose}
		>
			<RadioButtonGroup
				legendText="What this folder is"
				name="mailbox-special-use"
				valueSelected={String(choice)}
				onChange={(value) =>
					setChoice(
						value === "automatic" ? "automatic" : (Number(value) as SpecialUse),
					)
				}
			>
				<RadioButton
					id="special-use-automatic"
					labelText={`Detect automatically (currently: ${describeSpecialUse(mailbox.specialUse)})`}
					value="automatic"
				/>
				<RadioButton
					id="special-use-sent"
					labelText="Sent"
					value={String(SpecialUse.Sent)}
				/>
				<RadioButton
					id="special-use-drafts"
					labelText="Drafts"
					value={String(SpecialUse.Drafts)}
				/>
				<RadioButton
					id="special-use-trash"
					labelText="Trash"
					value={String(SpecialUse.Trash)}
				/>
				<RadioButton
					id="special-use-archive"
					labelText="Archive"
					value={String(SpecialUse.Archive)}
				/>
				<RadioButton
					id="special-use-junk"
					labelText="Junk"
					value={String(SpecialUse.Junk)}
				/>
				<RadioButton
					id="special-use-none"
					labelText="None of these — an ordinary folder"
					value={String(SpecialUse.None)}
				/>
			</RadioButtonGroup>
		</Modal>
	);
}

function describeSpecialUse(specialUse: SpecialUse): string {
	switch (specialUse) {
		case SpecialUse.Inbox:
			return "Inbox";
		case SpecialUse.Sent:
			return "Sent";
		case SpecialUse.Drafts:
			return "Drafts";
		case SpecialUse.Trash:
			return "Trash";
		case SpecialUse.Archive:
			return "Archive";
		case SpecialUse.Junk:
			return "Junk";
		default:
			return "an ordinary folder";
	}
}

/**
 * Bounds one mailbox's initial sync differently from the account's own choice (§3, §13 Epic 3)
 * — offered only before this mailbox's own backfill has started, since changing it afterward
 * has nothing left to bound.
 */
function SyncOverrideModal({
	mailbox,
	onSubmit,
	onClose,
}: {
	mailbox: Mailbox;
	onSubmit: (mode: InitialSyncMode | null, boundValue: number | null) => void;
	onClose: () => void;
}) {
	const [mode, setMode] = useState<InitialSyncMode | "account-default">(
		mailbox.initialSyncModeOverride ?? "account-default",
	);
	const [boundValue, setBoundValue] = useState(
		mailbox.initialSyncBoundValueOverride ?? 3,
	);

	return (
		<Modal
			open
			modalHeading={`Sync settings for "${mailbox.name}"`}
			primaryButtonText="Save"
			secondaryButtonText="Cancel"
			onRequestSubmit={() =>
				onSubmit(
					mode === "account-default" ? null : mode,
					mode === "account-default" || mode === InitialSyncMode.Full
						? null
						: boundValue,
				)
			}
			onRequestClose={onClose}
		>
			<RadioButtonGroup
				legendText="Initial sync for this folder"
				name="mailbox-sync-mode"
				valueSelected={String(mode)}
				onChange={(value) =>
					setMode(
						value === "account-default"
							? "account-default"
							: (Number(value) as InitialSyncMode),
					)
				}
			>
				<RadioButton
					id="mailbox-sync-default"
					labelText="Use the account's own setting"
					value="account-default"
				/>
				<RadioButton
					id="mailbox-sync-full"
					labelText="Full history"
					value={String(InitialSyncMode.Full)}
				/>
				<RadioButton
					id="mailbox-sync-months"
					labelText="Last N months"
					value={String(InitialSyncMode.LastNMonths)}
				/>
				<RadioButton
					id="mailbox-sync-messages"
					labelText="Last N messages"
					value={String(InitialSyncMode.LastNMessages)}
				/>
			</RadioButtonGroup>
			{mode === InitialSyncMode.LastNMonths ||
			mode === InitialSyncMode.LastNMessages ? (
				<NumberInput
					id="mailbox-sync-bound"
					label={mode === InitialSyncMode.LastNMonths ? "Months" : "Messages"}
					min={1}
					value={boundValue}
					onChange={(_, { value }) => setBoundValue(Number(value))}
				/>
			) : null}
		</Modal>
	);
}

/**
 * Whether `candidate` is `ancestorId` itself or sits anywhere beneath it.
 *
 * Guards the reparent drop: a folder dropped onto its own descendant would give that
 * descendant's `ParentId` chain a cycle, which is what {@link renderLevel}'s recursion walks —
 * an infinite tree with no way back out.
 */
function isDescendantOf(
	mailboxes: Mailbox[],
	candidate: Mailbox,
	ancestorId: string,
): boolean {
	let current: Mailbox | undefined = candidate;
	while (current) {
		if (current.id === ancestorId) return true;
		current = mailboxes.find((m) => m.id === current!.parentId);
	}
	return false;
}

/**
 * Reorder (move up/down among siblings) and reparent ("Move to…") previously had no
 * keyboard/context-menu path at all — only reachable by dragging a folder onto a sibling or a
 * different parent (§13 Epic 2), the identical gap pass 201 closed for moving a *message* into
 * a folder. A pure function, like `messageActions`, so the menu contents can be tested without
 * rendering the tree.
 */
export function mailboxMoveActions(
	mailboxes: Mailbox[],
	mailbox: Mailbox,
	reorder: (orderedMailboxIds: string[]) => void,
	reparent: (newParentId: string | null) => void,
) {
	const siblingIds = mailboxes
		.filter((m) => (m.parentId ?? null) === (mailbox.parentId ?? null))
		.map((m) => m.id);
	const index = siblingIds.indexOf(mailbox.id);

	const swapWith = (otherIndex: number) => {
		const reordered = [...siblingIds];
		[reordered[index], reordered[otherIndex]] = [
			reordered[otherIndex],
			reordered[index],
		];
		reorder(reordered);
	};

	// "Move to" lists every mailbox this folder could legally reparent under: not itself, not
	// its current parent (a no-op MoveMailbox call), and not one of its own descendants —
	// reparenting under a descendant would cycle ParentId, which renderLevel's recursion has
	// no way back out of. This mirrors dropOnMailbox's own folder-drag branch, which likewise
	// never guarded the *target* against being synthesized — reparenting a real folder under a
	// synthesized Gmail label-group intermediate is fine, since MoveMailboxAsync only needs
	// that target's local path, not a real label id of its own.
	const reparentTargets = mailboxes
		.filter(
			(candidate) =>
				candidate.id !== mailbox.id &&
				candidate.id !== (mailbox.parentId ?? undefined) &&
				!isDescendantOf(mailboxes, candidate, mailbox.id),
		)
		.sort((a, b) => a.name.localeCompare(b.name));

	// Reparenting the mailbox *being moved* is a different story when it's synthesized: Gmail's
	// MoveMailboxAsync needs that mailbox's own provider id to PATCH, and a synthesized row has
	// none (it's a local label-group intermediate the sidebar derived, not a real label) —
	// GmailMailProvider.ProviderMailboxId() throws for it. RunProviderCallAsync does turn that
	// into a clean HubException rather than a raw crash, but its message ("Gmail mailboxes
	// always have a provider id") is an internal assertion, not the same guidance rename/delete
	// already give for this exact row shape — so it's disabled here the same way, rather than
	// letting the user reach a confusing error.
	const synthesizedReparentMessage = mailbox.isSynthesized
		? "Gmail doesn't support moving a nested label group directly — move the label itself in Gmail."
		: undefined;

	return [
		{
			label: "Move up",
			run: () => swapWith(index - 1),
			unavailable: index <= 0 ? "Already first" : undefined,
		},
		{
			label: "Move down",
			run: () => swapWith(index + 1),
			unavailable:
				index === -1 || index >= siblingIds.length - 1
					? "Already last"
					: undefined,
		},
		{
			label: "Move to",
			run: () => undefined,
			unavailable:
				synthesizedReparentMessage ??
				(reparentTargets.length === 0
					? "No other folders to move this into"
					: undefined),
			children: reparentTargets.map((target) => ({
				label: target.name,
				run: () => reparent(target.id),
			})),
		},
		{
			label: "Move to top level",
			run: () => reparent(null),
			unavailable:
				synthesizedReparentMessage ??
				(mailbox.parentId === null ? "Already at the top level" : undefined),
		},
	];
}

/**
 * What deleting this folder will do, according to the provider.
 *
 * Deleting an IMAP or Graph folder destroys the mail inside it; deleting a Gmail label does
 * not. Until the answer is known the wording commits to neither — a confirmation that
 * guesses is worse than one that waits, because the user acts on it (§2).
 */
function describeDeletion(capabilities: Capabilities | undefined): string {
	if (capabilities === undefined) {
		return "Checking what this will do to the messages in it…";
	}

	return capabilities.deletingMailboxDeletesMessages
		? "The messages in this folder will be deleted with it. This cannot be undone."
		: "This removes the label. Its messages stay in All Mail.";
}

/**
 * The provider's count where there is one, and the local count otherwise.
 *
 * Under a bounded sync the local count is simply wrong as a mailbox total — "last 3 months"
 * of a large inbox holds a fraction of it — so where the server tells us, that is what the
 * sidebar shows. Where it cannot, the local count is shown as what it is (§1).
 */
/**
 * "Fetched of estimated" while a mailbox is still backfilling (§13 Epic 3).
 *
 * Reads a cache entry `SyncProgress` events write directly, never fetches one itself: there is
 * no request that would ever produce this value on its own, only the push from the hub. Still
 * a real `useQuery` subscription (`enabled: false` only suppresses fetching, not the observer),
 * so this re-renders on every `SyncProgress` event for this mailbox without any prop drilling.
 */
function BackfillProgress({ mailboxId }: { mailboxId: string }) {
	const { data: progress } = useQuery({
		queryKey: queryKeys.syncProgress(mailboxId),
		queryFn: () => undefined as SyncProgress | undefined,
		enabled: false,
	});

	if (!progress) return <span className={styles.count}>Syncing…</span>;

	return (
		<span className={styles.count}>
			{progress.estimatedTotal
				? `${progress.messagesFetched} of ${progress.estimatedTotal}`
				: `${progress.messagesFetched} synced`}
		</span>
	);
}

function describeCount(mailbox: Mailbox): string {
	if (mailbox.providerUnreadCount !== null && mailbox.providerUnreadCount > 0) {
		return `${mailbox.providerUnreadCount}`;
	}

	return mailbox.providerTotalCount === null
		? `${mailbox.localCount} held`
		: "";
}
