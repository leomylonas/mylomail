import { describe, expect, it } from "vitest";
import type { MessageSummaryDto } from "@mylomail/shared-types/SignalR/MyloMail.Api.Contracts";
import { mergeServerKnownProjection } from "@mylomail/renderer/Shell/Backend/HubConnection";

function summary(
	overrides: Partial<MessageSummaryDto> = {},
): MessageSummaryDto {
	return {
		id: "message",
		accountId: "account",
		subject: "Subject",
		snippet: "",
		from: [],
		receivedAt: "2026-01-01T00:00:00Z",
		isRead: false,
		isFlagged: false,
		hasNonInlineAttachments: false,
		threadMessageCount: 1,
		...overrides,
	};
}

describe("mergeServerKnownProjection", () => {
	it("does not let an older mutation event erase an acquired snippet", () => {
		const merged = mergeServerKnownProjection(
			summary({ snippet: "Body-derived preview" }),
			summary({ snippet: "", isRead: true }),
		);

		expect(merged.snippet).toBe("Body-derived preview");
		expect(merged.isRead).toBe(true);
	});

	it("fills a blank list preview from a content event", () => {
		const merged = mergeServerKnownProjection(
			summary(),
			summary({ snippet: "Body-derived preview", isFlagged: true }),
		);

		expect(merged.snippet).toBe("Body-derived preview");
		expect(merged.isFlagged).toBe(true);
	});
});
