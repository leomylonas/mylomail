import { describe, expect, it } from "vitest";
import { addressLabel } from "@mylomail/renderer/Lib/AddressDisplay";

describe("addressLabel", () => {
	it("uses the name when there is one", () => {
		expect(addressLabel({ name: "Leo", email: "leo@example.org" })).toBe("Leo");
	});

	it.each([null, undefined, "", "   "])(
		"falls back to the address for a name of %j",
		(name) => {
			expect(addressLabel({ name, email: "leo@example.org" })).toBe(
				"leo@example.org",
			);
		},
	);
});
