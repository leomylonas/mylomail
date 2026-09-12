import { createServer } from "node:http";
import { setTimeout as delay } from "node:timers/promises";
import { expect, test } from "@playwright/test";
import { RemoteContentRuleDecision } from "@mylomail/shared-types/Api/Domain/RemoteContentRuleDecision";
import { RemoteContentRuleScope } from "@mylomail/shared-types/Api/Domain/RemoteContentRuleScope";
import { launchApp } from "@mylomail/renderer-e2e/AppFixture";
import { createImapAccount } from "@mylomail/renderer-e2e/SeedAccount";
import {
	appendHostileHtmlMessage,
	clearInbox,
} from "@mylomail/renderer-e2e/SeedImap";

/**
 * The CONDSTORE tier of the local matrix (`pnpm imap:up`).
 *
 * Each spec uses a different tier so they cannot disturb one another's mailbox — they share
 * no state, and the suite covers three capability tiers rather than one.
 */
const imapPort = 12143;

/**
 * The reading pane against a message written to attack it (§13).
 *
 * Sanitisation is unit-tested, but only the real app can show that the isolated document, its
 * content policy, the blob-URL path for inline images and the remote-content block actually
 * compose — each is individually correct in ways that could still fail together.
 */
test("hostile HTML renders safely and blocks tracking", async () => {
	let remoteRequests = 0;
	let resolveFirstRemoteRequest: (() => void) | undefined;
	const firstRemoteRequest = new Promise<void>((resolve) => {
		resolveFirstRemoteRequest = resolve;
	});
	const trackingServer = createServer((_, response) => {
		remoteRequests++;
		resolveFirstRemoteRequest?.();
		response.writeHead(200, { "Content-Type": "image/gif" });
		response.end(
			Buffer.from("R0lGODlhAQABAIAAAAAAAP///ywAAAAAAQABAAACAUwAOw==", "base64"),
		);
	});
	await new Promise<void>((resolve) =>
		trackingServer.listen(0, "127.0.0.1", resolve),
	);
	const trackerAddress = trackingServer.address();
	if (!trackerAddress || typeof trackerAddress === "string")
		throw new Error("The tracking fixture did not bind a TCP port.");

	await clearInbox(imapPort);
	await appendHostileHtmlMessage(
		imapPort,
		"Hostile message",
		`http://127.0.0.1:${trackerAddress.port}/pixel.gif`,
	);

	const { app, window } = await launchApp();

	try {
		await createImapAccount(window, imapPort);

		await window
			.getByRole("button", { name: /INBOX/ })
			.click({ timeout: 60_000 });
		await window
			.getByRole("button", { name: /Hostile message/ })
			.click({ timeout: 60_000 });

		// The pane names the message it is showing.
		await expect(
			window.getByRole("heading", { name: "Hostile message", level: 2 }),
		).toBeVisible({ timeout: 60_000 });

		const body = window.frameLocator('iframe[title="Message body"]');
		await expect(body.locator("#visible-body")).toHaveText(
			"Hostile body text",
			{
				timeout: 60_000,
			},
		);

		// The script never ran, and cannot have: the frame's policy forbids script entirely,
		// and sanitisation removed the element before it got there.
		expect(await window.evaluate(() => "pwned" in window)).toBe(false);
		await expect(body.locator("script")).toHaveCount(0);

		// The tracking pixel is withheld until asked for, and the user is told why.
		await expect(body.locator("#tracker")).not.toHaveAttribute(
			"src",
			/tracker/,
		);
		await expect(window.getByText(/Remote content is blocked/)).toBeVisible();
		expect(remoteRequests).toBe(0);

		// Bypass sanitisation deliberately: the frame CSP remains the request-layer backstop
		// for references a parser misses. This image must not reach the local tracker.
		await body.locator("body").evaluate((element, source) => {
			const image = document.createElement("img");
			image.id = "csp-probe";
			image.src = source;
			element.append(image);
		}, `http://127.0.0.1:${trackerAddress.port}/csp-probe.gif`);
		const cspProbeEscaped = await Promise.race([
			firstRemoteRequest.then(() => true),
			delay(500).then(() => false),
		]);
		expect(cspProbeEscaped).toBe(false);
		expect(remoteRequests).toBe(0);
		await expect(
			window.locator('iframe[title="Message body"]'),
		).toHaveAttribute("sandbox", "allow-same-origin");

		// The renderer fetches the authenticated MIME part before passing the isolated frame a
		// blob URL; an img request cannot carry the launch credential itself.
		await expect(window.locator("[data-inline-status]")).toHaveAttribute(
			"data-inline-status",
			"resolved",
		);
		await expect
			.poll(() =>
				body
					.locator("#inline")
					.evaluate((image: HTMLImageElement) => image.naturalWidth),
			)
			.toBe(1);
		await expect(body.locator("#inline")).toHaveAttribute("src", /^blob:/);

		await window
			.getByText("Always allow images from example.org", { exact: true })
			.click();
		await window.getByRole("button", { name: "Load content" }).click();
		await expect(window.getByText(/Remote content is blocked/)).toBeHidden();
		await expect.poll(() => remoteRequests).toBe(1);

		await window.getByRole("button", { name: "Settings", exact: true }).click();
		await expect(
			window.getByText("Allow domain", { exact: true }),
		).toBeVisible();
		await expect(
			window.getByText("example.org", { exact: true }),
		).toBeVisible();
		await window
			.getByLabel("Decision")
			.selectOption(String(RemoteContentRuleDecision.Block));
		await window
			.getByLabel("Applies to")
			.selectOption(String(RemoteContentRuleScope.Domain));
		await window.getByLabel("Domain").fill("example.org");
		await window.getByRole("button", { name: "Add rule" }).click();
		await expect(
			window.getByText("Block domain", { exact: true }),
		).toBeVisible();
		await window.getByRole("button", { name: "Close", exact: true }).click();

		await expect(
			window.getByText(/blocked by your sender or domain policy/),
		).toBeVisible();
		await expect(
			window.getByRole("button", { name: "Load content" }),
		).toHaveCount(0);
	} finally {
		await app.close();
		trackingServer.closeAllConnections();
		await new Promise<void>((resolve, reject) =>
			trackingServer.close((error) => (error ? reject(error) : resolve())),
		);
	}
});
