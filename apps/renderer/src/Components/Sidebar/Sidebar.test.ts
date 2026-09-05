import { describe, expect, it } from "vitest";
import { authStateWarning } from "@mylomail/renderer/Components/Sidebar/Sidebar";
import { AuthState } from "@mylomail/shared-types/SignalR/MyloMail.Api.Domain";

describe("authStateWarning", () => {
	it("describes a reauthentication requirement", () => {
		expect(authStateWarning(AuthState.NeedsReauth)).toMatch(/reauthenticated/);
	});

	it("describes a locked credential store distinctly from a reauth prompt", () => {
		const message = authStateWarning(AuthState.CredentialStoreUnavailable);
		expect(message).toMatch(/keychain/);
		expect(message).not.toMatch(/reauthenticated/);
	});

	it("describes a generic connection error", () => {
		expect(authStateWarning(AuthState.Error)).toMatch(/connection problem/);
	});
});
