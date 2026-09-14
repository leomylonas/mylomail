import { describe, expect, it, vi } from "vitest";
import { saveComposeBeforeClose } from "@mylomail/renderer/Components/Compose/Compose";

describe("saveComposeBeforeClose", () => {
	it("does not save a draft after a send has been durably queued", async () => {
		const save = vi.fn<() => Promise<string>>();
		const awaitAttachmentMutations = vi.fn<() => Promise<void>>();

		await expect(
			saveComposeBeforeClose({
				isDiscarding: false,
				hasQueuedSend: true,
				save,
				awaitAttachmentMutations,
				reportFailure: vi.fn(),
			}),
		).resolves.toBe(true);

		expect(save).not.toHaveBeenCalled();
		expect(awaitAttachmentMutations).not.toHaveBeenCalled();
	});

	it("does not acknowledge close until attachment mutations have succeeded", async () => {
		let finishAttachment: (() => void) | undefined;
		const attachmentMutation = new Promise<void>((resolve) => {
			finishAttachment = resolve;
		});
		const close = saveComposeBeforeClose({
			isDiscarding: false,
			hasQueuedSend: false,
			save: vi.fn().mockResolvedValue("draft-1"),
			awaitAttachmentMutations: () => attachmentMutation,
			reportFailure: vi.fn(),
		});
		let acknowledged = false;
		void close.then(() => {
			acknowledged = true;
		});

		await Promise.resolve();
		expect(acknowledged).toBe(false);
		finishAttachment!();
		await expect(close).resolves.toBe(true);
	});

	it("rejects close acknowledgement when an attachment mutation fails", async () => {
		const failure = new Error("upload failed");
		const reportFailure = vi.fn();

		await expect(
			saveComposeBeforeClose({
				isDiscarding: false,
				hasQueuedSend: false,
				save: vi.fn().mockResolvedValue("draft-1"),
				awaitAttachmentMutations: () => Promise.reject(failure),
				reportFailure,
			}),
		).resolves.toBe(false);

		expect(reportFailure).toHaveBeenCalledWith(failure);
	});
});
