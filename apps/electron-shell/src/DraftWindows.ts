/**
 * Which window id (if any), other than the requester's own, is already editing `draftId` —
 * the pure lookup behind `focusDraftWindowChannel`. Kept separate from `Main.ts` so it has a
 * test seam: everything else in that file's IPC handlers touches `BrowserWindow`, which has no
 * fake here, but this map lookup has no Electron dependency at all.
 */
export function windowAlreadyEditing(
	draftWindows: ReadonlyMap<number, string | null>,
	requesterId: number | undefined,
	draftId: string,
): number | undefined {
	for (const [id, openDraftId] of draftWindows) {
		if (id !== requesterId && openDraftId === draftId) {
			return id;
		}
	}
	return undefined;
}
