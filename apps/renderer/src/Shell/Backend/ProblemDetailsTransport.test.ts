import { afterEach, describe, expect, it, vi } from "vitest";
import type { HubConnection } from "@microsoft/signalr";
import { ErrorCategory } from "@mylomail/shared-types/SignalR/MyloMail.Api.Errors";
import {
	fetchApi,
	MutationTransportError,
	normalizeHubErrors,
	notificationForError,
} from "@mylomail/renderer/Shell/Backend/ProblemDetailsTransport";

afterEach(() => vi.unstubAllGlobals());

describe("problem-details transport", () => {
	it("deserializes REST problems and preserves extension-backed actions", async () => {
		vi.stubGlobal(
			"fetch",
			vi.fn().mockResolvedValue(
				new Response(
					JSON.stringify({
						title: "Certificate rejected",
						detail: "The certificate changed.",
						status: 400,
						category: ErrorCategory.Validation,
						hostname: "imap.example.test",
						sha256Fingerprint: "AA:BB",
					}),
					{
						status: 400,
						headers: { "Content-Type": "application/problem+json" },
					},
				),
			),
		);

		const error = await fetchApi("/accounts").catch(
			(failure: unknown) => failure,
		);
		expect(error).toBeInstanceOf(MutationTransportError);
		expect((error as MutationTransportError).presentation).toMatchObject({
			title: "Certificate untrusted",
			action: "trust-certificate",
			certificate: {
				hostname: "imap.example.test",
				sha256Fingerprint: "AA:BB",
			},
		});
	});

	it("deserializes JSON from SignalR's HubException prefix", async () => {
		const invoke = vi.fn().mockRejectedValue(
			new Error(
				`An unexpected error occurred invoking 'SaveCalendarEvent' on the server. HubException: ${JSON.stringify(
					{
						status: 409,
						category: ErrorCategory.Conflict,
						detail: "The event changed elsewhere.",
					},
				)}`,
			),
		);
		const hub = { invoke } as unknown as HubConnection;
		normalizeHubErrors(hub);

		const error = await hub
			.invoke("SaveCalendarEvent")
			.catch((failure: unknown) => failure);
		expect(error).toBeInstanceOf(MutationTransportError);
		expect(notificationForError(error, "fallback")).toMatchObject({
			title: "Changed somewhere else",
			detail: "The event changed elsewhere.",
			persistent: true,
		});
	});

	it("presents authentication failures as persistent reauthentication notifications", () => {
		const error = new MutationTransportError({
			category: ErrorCategory.Auth,
			detail: "Provider detail",
			extensions: {},
		});

		expect(notificationForError(error, "fallback")).toMatchObject({
			title: "Sign in again",
			detail: "Provider detail",
			persistent: true,
		});
	});

	it("does not create a notification while a provider rate limit is in effect", () => {
		const error = new MutationTransportError({
			category: ErrorCategory.RateLimit,
			detail: "Provider detail",
			extensions: {},
		});

		expect(notificationForError(error, "fallback")).toBeNull();
	});

	it("classifies a fetch failure as transient network loss", async () => {
		vi.stubGlobal(
			"fetch",
			vi.fn().mockRejectedValue(new TypeError("fetch failed")),
		);

		const error = await fetchApi("/accounts").catch(
			(failure: unknown) => failure,
		);
		expect(notificationForError(error, "fallback")).toEqual({
			kind: "error",
			title: "You appear to be offline",
			detail: "MyloMail will carry on once the connection returns.",
			persistent: false,
			action: undefined,
		});
	});
});
