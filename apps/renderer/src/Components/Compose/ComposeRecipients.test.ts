import { describe, expect, it } from "vitest";
import { appendRecipient } from "@mylomail/renderer/Components/Compose/Compose";

describe("compose contact completion", () => {
	it("appends a raw address without replacing existing recipients", () => {
		expect(appendRecipient("first@example.test", "ada@example.test")).toBe(
			"first@example.test, ada@example.test",
		);
	});

	it("does not append an address already present with different casing", () => {
		expect(appendRecipient("ADA@example.test", "ada@example.test")).toBe(
			"ADA@example.test",
		);
	});
});
