import { describe, expect, it } from "vitest";
import { SpecialUse } from "@mylomail/shared-types/SignalR/MyloMail.Api.Domain";
import type { ExportFolderDto } from "@mylomail/shared-types/SignalR/MyloMail.Api.Contracts";
import {
	buildExportTree,
	checkState,
	describeSelection,
	toggleNode,
} from "@mylomail/renderer/Components/ExportWizard/ExportSelection";

function folder(
	id: string,
	parentId: string | undefined,
	extra: Partial<ExportFolderDto> = {},
): ExportFolderDto {
	return {
		mailboxId: id,
		parentId: parentId,
		name: id,
		specialUse: SpecialUse.None,
		messageCount: 10,
		notDownloadedCount: 0,
		isFullyDownloaded: true,
		...extra,
	};
}

const folders = [
	folder("b", "root"),
	folder("a", "root"),
	folder("root", undefined),
	folder("a1", "a"),
];

describe("export folder selection", () => {
	const [root] = buildExportTree(folders);

	it("nests folders under their parent, siblings by name", () => {
		expect(root!.folder.mailboxId).toBe("root");
		expect(root!.children.map((c) => c.folder.mailboxId)).toEqual(["a", "b"]);
		expect(root!.children[0]!.children[0]!.folder.mailboxId).toBe("a1");
	});

	it("ticking a folder takes its subfolders with it, and unticking clears them", () => {
		const all = toggleNode(root!, new Set());
		expect([...all].sort()).toEqual(["a", "a1", "b", "root"]);
		expect(checkState(root!, all)).toBe("checked");
		expect(toggleNode(root!, all).size).toBe(0);
	});

	it("a folder with only some of its subtree selected is partial, and ticking it completes the subtree", () => {
		const onlyB = new Set(["b"]);
		expect(checkState(root!, onlyB)).toBe("partial");
		expect(checkState(root!.children[1]!, onlyB)).toBe("checked");
		expect(toggleNode(root!, onlyB).size).toBe(4);
	});

	it("describes what is chosen, including what will not be fully there", () => {
		const withGaps = [
			folder("x", undefined, { messageCount: 5, notDownloadedCount: 2 }),
			folder("y", undefined, { messageCount: 7, isFullyDownloaded: false }),
			folder("z", undefined, { messageCount: 100 }),
		];
		expect(describeSelection(withGaps, new Set(["x", "y"]))).toEqual({
			folderCount: 2,
			messageCount: 12,
			notDownloaded: 2,
			incompleteFolders: ["y"],
		});
	});
});
