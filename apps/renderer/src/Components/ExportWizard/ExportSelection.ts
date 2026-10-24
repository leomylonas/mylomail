import type { ExportFolderDto } from "@mylomail/shared-types/SignalR/MyloMail.Api.Contracts";

export interface ExportTreeNode {
	folder: ExportFolderDto;
	children: ExportTreeNode[];
}

export type CheckState = "checked" | "unchecked" | "partial";

/** The account's folders as a tree, siblings by name; a folder whose parent is unknown is a root. */
export function buildExportTree(
	folders: readonly ExportFolderDto[],
): ExportTreeNode[] {
	const nodes = new Map(
		folders.map((folder) => [
			folder.mailboxId,
			{ folder, children: [] as ExportTreeNode[] },
		]),
	);
	const roots: ExportTreeNode[] = [];
	for (const node of nodes.values()) {
		const parent = node.folder.parentId
			? nodes.get(node.folder.parentId)
			: undefined;
		(parent ? parent.children : roots).push(node);
	}
	const sort = (list: ExportTreeNode[]) => {
		list.sort((a, b) => a.folder.name.localeCompare(b.folder.name));
		for (const node of list) sort(node.children);
	};
	sort(roots);
	return roots;
}

function subtreeIds(node: ExportTreeNode): string[] {
	return [node.folder.mailboxId, ...node.children.flatMap(subtreeIds)];
}

/** A folder is checked when it and everything under it is, partial when only some of that is. */
export function checkState(
	node: ExportTreeNode,
	selected: ReadonlySet<string>,
): CheckState {
	const ids = subtreeIds(node);
	const count = ids.filter((id) => selected.has(id)).length;
	return count === 0
		? "unchecked"
		: count === ids.length
			? "checked"
			: "partial";
}

/** Ticking a folder selects it and all its subfolders; unticking clears them all. A partial folder becomes fully ticked. */
export function toggleNode(
	node: ExportTreeNode,
	selected: ReadonlySet<string>,
): Set<string> {
	const next = new Set(selected);
	const select = checkState(node, selected) !== "checked";
	for (const id of subtreeIds(node)) {
		if (select) next.add(id);
		else next.delete(id);
	}
	return next;
}

export function describeSelection(
	folders: readonly ExportFolderDto[],
	selected: ReadonlySet<string>,
): {
	folderCount: number;
	messageCount: number;
	sizeBytes: number;
	notDownloaded: number;
	incompleteFolders: string[];
} {
	const chosen = folders.filter((folder) => selected.has(folder.mailboxId));
	return {
		folderCount: chosen.length,
		messageCount: chosen.reduce((sum, f) => sum + f.messageCount, 0),
		sizeBytes: chosen.reduce((sum, f) => sum + f.sizeBytes, 0),
		notDownloaded: chosen.reduce((sum, f) => sum + f.notDownloadedCount, 0),
		incompleteFolders: chosen
			.filter((folder) => !folder.isFullyDownloaded)
			.map((folder) => folder.name),
	};
}

/** "1.4 GB", "820 MB": decimal units, as a disk or a file manager reports them. */
export function formatBytes(bytes: number): string {
	const units = ["B", "KB", "MB", "GB", "TB"];
	let value = bytes;
	let unit = 0;
	while (value >= 1000 && unit < units.length - 1) {
		value /= 1000;
		unit++;
	}
	return `${unit === 0 || value >= 100 ? Math.round(value) : value.toFixed(1)} ${units[unit]}`;
}
