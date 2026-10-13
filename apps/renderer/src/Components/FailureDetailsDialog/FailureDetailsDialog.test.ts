import { describe, expect, it } from "vitest";
import { describeOperation } from "@mylomail/renderer/Components/FailureDetailsDialog/FailureDetailsDialog";
import { MutationOperationKind } from "@mylomail/shared-types/SignalR/MyloMail.Api.Domain";

describe("describeOperation", () => {
	it("names every kind of change in the user's terms", () => {
		const names = [
			MutationOperationKind.SetFlags,
			MutationOperationKind.MoveMessage,
			MutationOperationKind.RemoveFromMailbox,
			MutationOperationKind.MoveToTrash,
			MutationOperationKind.DeletePermanently,
			MutationOperationKind.Send,
		].map(describeOperation);

		expect(new Set(names).size).toBe(names.length);
		expect(describeOperation(MutationOperationKind.DeletePermanently)).toBe(
			"Delete permanently",
		);
	});
});
