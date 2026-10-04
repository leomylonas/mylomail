import { describe, expect, it } from "vitest";
import { withSidebarWidth } from "@mylomail/renderer/Shell/Layout/UseShellLayout";

describe("withSidebarWidth", () => {
	it("keeps the list and detail ratio and the total at 100", () => {
		const resized = withSidebarWidth({ sidebar: 20, list: 35, detail: 45 }, 30);

		expect(resized.sidebar).toBe(30);
		expect(resized.list / resized.detail).toBeCloseTo(35 / 45);
		expect(resized.sidebar + resized.list + resized.detail).toBeCloseTo(100);
	});

	it("splits the remainder evenly when neither panel has a share", () => {
		expect(withSidebarWidth({ sidebar: 20, list: 0, detail: 0 }, 40)).toEqual({
			sidebar: 40,
			list: 30,
			detail: 30,
		});
	});
});
