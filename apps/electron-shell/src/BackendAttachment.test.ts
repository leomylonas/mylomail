import { describe, expect, it, vi } from "vitest";
import { attachBackend } from "@mylomail/electron-shell/BackendAttachment";

describe("attachBackend", () => {
	it("authenticates and waits for the configured loopback backend", async () => {
		const waitUntilReady = vi.fn(async () => undefined);

		await expect(
			attachBackend({
				backendUrl: "http://127.0.0.1:5123/",
				launchToken: " token-abc ",
				waitUntilReady,
			}),
		).resolves.toEqual({
			origin: "http://127.0.0.1:5123",
			launchToken: "token-abc",
		});
		expect(waitUntilReady).toHaveBeenCalledWith(
			"http://127.0.0.1:5123",
			"token-abc",
		);
	});

	it("requires both attach settings before probing", async () => {
		const waitUntilReady = vi.fn(async () => undefined);

		await expect(
			attachBackend({ launchToken: "token", waitUntilReady }),
		).rejects.toThrow(/BACKEND_URL is required/);
		await expect(
			attachBackend({
				backendUrl: "http://127.0.0.1:5123",
				waitUntilReady,
			}),
		).rejects.toThrow(/MYLOMAIL_LAUNCH_TOKEN is required/);
		expect(waitUntilReady).not.toHaveBeenCalled();
	});

	it.each([
		"https://127.0.0.1:5123",
		"http://localhost:5123",
		"http://192.0.2.10:5123",
		"http://127.0.0.1",
		"http://user:secret@127.0.0.1:5123",
		"http://127.0.0.1:5123/api",
		"http://127.0.0.1:5123/?token=secret",
	])(
		"rejects a non-origin or non-loopback BACKEND_URL: %s",
		async (backendUrl) => {
			await expect(
				attachBackend({
					backendUrl,
					launchToken: "token",
					waitUntilReady: async () => undefined,
				}),
			).rejects.toThrow(/BACKEND_URL/);
		},
	);

	it("does not create a renderer connection until health succeeds", async () => {
		await expect(
			attachBackend({
				backendUrl: "http://127.0.0.1:5123",
				launchToken: "token",
				waitUntilReady: async () => {
					throw new Error("health rejected the token");
				},
			}),
		).rejects.toThrow(/health rejected the token/);
	});
});
