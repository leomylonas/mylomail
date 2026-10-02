import { describe, expect, it } from "vitest";
import { relaxRemoteImageHeaders } from "@mylomail/electron-shell/RemoteImagePolicy";

const corp = {
	"Cross-Origin-Resource-Policy": ["same-origin"],
	"Content-Type": ["image/png"],
};

describe("relaxRemoteImageHeaders", () => {
	it("drops the resource policy from a remote image, whatever the header's case", () => {
		expect(
			relaxRemoteImageHeaders("image", "https://claude.ai/a.png", corp),
		).toEqual({
			"Content-Type": ["image/png"],
		});
		expect(
			relaxRemoteImageHeaders("image", "https://x.test/a.png", {
				"cross-origin-resource-policy": ["same-site"],
			}),
		).toEqual({});
	});

	it("leaves a response alone when there is nothing to relax", () => {
		expect(
			relaxRemoteImageHeaders("image", "https://x.test/a.png", {
				"Content-Type": ["image/png"],
			}),
		).toBeNull();
	});

	it("never touches anything but images", () => {
		for (const type of [
			"script",
			"xhr",
			"mainFrame",
			"subFrame",
			"stylesheet",
			"font",
		])
			expect(
				relaxRemoteImageHeaders(type, "https://x.test/a", corp),
			).toBeNull();
	});

	it("never touches the app's own origin", () => {
		expect(
			relaxRemoteImageHeaders("image", "http://127.0.0.1:5000/a.png", corp),
		).toBeNull();
		expect(
			relaxRemoteImageHeaders("image", "http://localhost/a.png", corp),
		).toBeNull();
	});
});
