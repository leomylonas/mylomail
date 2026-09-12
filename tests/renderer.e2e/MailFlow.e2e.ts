import { expect, test } from "@playwright/test";
import { launchApp } from "@mylomail/renderer-e2e/AppFixture";
import { createImapAccount } from "@mylomail/renderer-e2e/SeedAccount";
import {
	appendMessage,
	clearFolder,
	clearInbox,
	countWithSubject,
	inboxFlags,
} from "@mylomail/renderer-e2e/SeedImap";

/**
 * The QRESYNC tier of the local matrix of the local matrix (`pnpm imap:up`).
 *
 * Each spec uses a different tier so they cannot disturb one another's mailbox — they share
 * no state, and the suite covers three capability tiers rather than one.
 */
const imapPort = 11143;

/**
 * The whole product, driven the way a user drives it.
 *
 * Everything below the UI is covered by unit and fault-injection tests, and all of it passed
 * while a mutation enqueued from the hub never reached the server — the database looked
 * right the entire time. This test is the one that would have caught that, because it asks
 * the IMAP server what happened rather than asking the app what it believes.
 */
test("a real account syncs, lists mail, and its flag changes reach the server", async () => {
	await clearInbox(imapPort);
	await clearFolder(imapPort, "Trash");
	await appendMessage(
		imapPort,
		"First message",
		"sender@example.org",
		"Quarterly forecast details.",
	);
	await appendMessage(imapPort, "Second message");

	const { app, window } = await launchApp();

	try {
		await expect(
			window.getByRole("heading", { name: "MyloMail" }),
		).toBeVisible();

		// Adding the account through the same API the settings UI will use.
		await createImapAccount(window, imapPort);

		// Topology discovery, then coverage: the sidebar fills in as they land.
		const inboxMailbox = window.getByRole("button", { name: /INBOX/ });
		await expect(inboxMailbox).toBeVisible({ timeout: 60_000 });
		await inboxMailbox.click();

		const firstMessage = window.getByRole("button", { name: /First message/ });
		await expect(firstMessage).toBeVisible({ timeout: 60_000 });
		await expect(
			window.getByRole("button", { name: /Second message/ }),
		).toBeVisible();
		await expect(inboxMailbox).toContainText("2 unread · 2 total");

		for (const column of ["From", "Subject", "Snippet", "Date", "Read", "Flag"])
			await expect(
				window.getByRole("button", { name: `Sort by ${column}`, exact: true }),
			).toBeVisible();
		await window.getByLabel("Date", { exact: true }).selectOption("today");
		await expect(firstMessage).toBeVisible();
		await window.getByLabel("Date", { exact: true }).selectOption("all");

		// Nothing is read on the server yet.
		expect((await inboxFlags(imapPort)).join(" ")).not.toContain("\\Seen");

		await firstMessage.click();

		// Selecting a message opens it. Content is background work, so the body arrives after
		// the metadata does — this asserts the whole acquisition path, not just the click.
		await expect(
			window.getByRole("article", { name: "Message" }),
		).toBeVisible();
		await expect(
			window
				.getByRole("article", { name: "Message" })
				.getByText("Quarterly forecast details."),
		).toBeVisible({
			timeout: 60_000,
		});
		await expect(firstMessage).toContainText("Quarterly forecast details.", {
			timeout: 60_000,
		});

		// Printed output is the actual reading surface: canonical headers, the acquired body,
		// and no renderer-owned browser print call. Replace only Electron's native print method
		// so the IPC path can be observed without opening an OS dialog in headless CI.
		const article = window.getByRole("article", { name: "Message" });
		await expect(
			article.getByText("Someone <sender@example.org>"),
		).toBeVisible();
		await expect(article.getByText("test@mylomail.local")).toBeVisible();
		await expect(article.locator("dt", { hasText: "Date" })).toBeVisible();
		await window.emulateMedia({ media: "print" });
		await expect(window.locator("#sidebar")).toBeHidden();
		await expect(window.locator("#list")).toBeHidden();
		await expect(article).toBeVisible();
		await expect(
			article.getByText("Quarterly forecast details."),
		).toBeVisible();
		await expect(article.getByRole("button", { name: "Print" })).toBeHidden();
		await window.emulateMedia({ media: "screen" });
		await app.evaluate(({ BrowserWindow }) => {
			const webContents = BrowserWindow.getAllWindows()[0]?.webContents;
			if (!webContents) throw new Error("No printable window.");
			// Playwright exposes Electron's concrete WebContents type, whose method is writable
			// at runtime but not modeled as replaceable; this named adapter is test-local.
			const printableWebContents = webContents as unknown as {
				print: (
					options: unknown,
					callback: (success: boolean, failureReason: string) => void,
				) => void;
			};
			printableWebContents.print = (options, callback) => {
				Reflect.set(globalThis, "__mylomailPrintOptions", options);
				callback(true, "");
			};
		});
		const print = article.getByRole("button", { name: "Print" });
		await expect(print).toBeEnabled();
		await print.click();
		await expect
			.poll(() =>
				app.evaluate((electron) => {
					void electron;
					return Reflect.get(globalThis, "__mylomailPrintOptions");
				}),
			)
			.toMatchObject({ printBackground: true });

		// The mutation is enqueued locally, executed by a job, and applied by the provider.
		// Asking the server is the point: the local database would look correct either way.
		await expect
			.poll(
				async () =>
					(await inboxFlags(imapPort)).filter((f) => f.includes("\\Seen"))
						.length,
				{
					timeout: 60_000,
					message: "the flag never reached the IMAP server",
				},
			)
			.toBe(1);
		await expect(inboxMailbox).toContainText("1 unread · 2 total", {
			timeout: 90_000,
		});

		await window.getByLabel("Read", { exact: true }).selectOption("unread");
		await expect(firstMessage).toBeHidden({ timeout: 60_000 });
		await expect(
			window.getByRole("button", { name: /Second message/ }),
		).toBeVisible();
		await window.getByLabel("Read", { exact: true }).selectOption("all");

		const readSort = window.getByRole("button", {
			name: /Sort by Read/,
		});
		await readSort.click();
		await expect(readSort).toHaveAttribute("aria-pressed", "true");
		if ((await readSort.getAttribute("aria-label"))?.includes("descending"))
			await readSort.click();
		await expect(readSort).toHaveAttribute("aria-label", /ascending/);
		await expect
			.poll(async () => {
				const labels = await window
					.locator("#message-list")
					.getByRole("button")
					.allTextContents();
				return labels
					.filter((label) => /First message|Second message/.test(label))
					.map((label) =>
						label.includes("First message") ? "First" : "Second",
					);
			})
			.toEqual(["Second", "First"]);

		const localFilter = window.getByPlaceholder(
			"Filter sender, subject, snippet, or date…",
		);
		await localFilter.fill("Quarterly forecast");
		await expect(firstMessage).toBeVisible();
		await expect(
			window.getByRole("button", { name: /Second message/ }),
		).toBeHidden();
		await localFilter.fill("");
		// Search reads the FTS index that content acquisition populated, and scopes to the
		// selected mailbox after the match (§8).
		const search = window.getByRole("searchbox", { name: /Search mail/ });
		await search.fill("Second");
		await expect(
			window.getByRole("button", { name: /Second message/ }),
		).toBeVisible();
		await expect(
			window.getByRole("button", { name: /First message/ }),
		).toBeHidden();

		// Field-scoped queries work because the index has real columns rather than one blob.
		await search.fill("Subject:First");
		await expect(
			window.getByRole("button", { name: /First message/ }),
		).toBeVisible();
		await expect(
			window.getByRole("button", { name: /Second message/ }),
		).toBeHidden();

		await search.fill("");
		await expect(
			window.getByRole("button", { name: /Second message/ }),
		).toBeVisible();

		// The conventional message menu (§13). Reply is wired to the compose panel now, so
		// the entry acts rather than sitting disabled; multi-select is what disables it.
		await window
			.getByRole("button", { name: /First message/ })
			.click({ button: "right" });
		const menu = window.getByRole("menu", { name: "Message actions" });
		await expect(menu).toBeVisible();
		await expect(
			menu.getByRole("menuitem", { name: "Reply", exact: true }),
		).toBeEnabled();
		await expect(menu.getByRole("menuitem", { name: "Flag" })).toBeEnabled();

		// Acting through the menu reaches the server, exactly as clicking does. Flagging
		// rather than toggling read, because the label for read depends on what earlier steps
		// left behind and an assertion that reads differently on a re-run is not one.
		await menu.getByRole("menuitem", { name: "Flag" }).click();
		await expect(
			window.getByRole("button", { name: /First message/ }),
		).toContainText("Flagged");
		await expect
			.poll(
				async () =>
					(await inboxFlags(imapPort)).filter((f) => f.includes("\\Flagged"))
						.length,
				{
					timeout: 60_000,
					message: "flagging from the menu never reached the server",
				},
			)
			.toBe(1);

		await window.getByLabel("Flag", { exact: true }).selectOption("flagged");
		await expect(
			window.getByRole("button", { name: /First message/ }),
		).toBeVisible({ timeout: 60_000 });
		await expect(
			window.getByRole("button", { name: /Second message/ }),
		).toBeHidden();
		await window.getByLabel("Flag", { exact: true }).selectOption("all");

		// A change the server refuses is shown, not swallowed. The message is deleted from the
		// server behind the app's back, so the mutation fails on its own terms.
		await clearInbox(imapPort);
		await window.getByRole("button", { name: /Second message/ }).click();
		await expect(
			window.getByRole("region", { name: "Notifications" }),
		).toContainText(/refused|wrong|went wrong/i, { timeout: 90_000 });

		// New mail arriving on the server reaches an open window without anything else
		// prompting it — the point of raising MessageReceived at all (§7).
		await appendMessage(imapPort, "Third message");
		await expect(
			window.getByRole("button", { name: /Third message/ }),
		).toBeVisible({
			timeout: 90_000,
		});

		const thirdMessage = window.getByRole("button", { name: /Third message/ });
		await thirdMessage.click({ button: "right" });
		await menu.getByRole("menuitem", { name: "Move to trash" }).click();
		await expect(thirdMessage).toBeHidden();
		await expect
			.poll(() => countWithSubject(imapPort, "Trash", "Third message"), {
				timeout: 60_000,
				message: "the optimistically hidden message never reached Trash",
			})
			.toBe(1);

		// Existing accounts expose the same initial-sync choice as onboarding. The returned,
		// normalised values flow through the live account projection, so closing and reopening
		// the pane proves the choice is persisted rather than only retained in component state.
		await window.getByRole("button", { name: "Account settings" }).click();
		await window.getByText("Last N messages", { exact: true }).click();
		const syncBound = window.getByLabel("Messages", { exact: true });
		await syncBound.fill("1");
		await window.getByRole("button", { name: "Save", exact: true }).click();
		await expect(window.getByText("Saved.", { exact: true })).toBeVisible();
		await window.getByRole("button", { name: "Close", exact: true }).click();
		await window.getByRole("button", { name: "Account settings" }).click();
		await expect(window.getByLabel("Last N messages")).toBeChecked();
		await expect(window.getByLabel("Messages", { exact: true })).toHaveValue(
			"1",
		);
	} finally {
		await app.close();
	}
});
