import { expect, test } from "@playwright/test";
import { launchApp } from "@mylomail/renderer-e2e/AppFixture";
import { createImapAccount } from "@mylomail/renderer-e2e/SeedAccount";
import { clearInbox } from "@mylomail/renderer-e2e/SeedImap";

const imapPort = 11143;
const mailpitApi = "http://127.0.0.1:18025/api/v1";

/** A one-pixel PNG. */
const pixel =
	"iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==";

/**
 * A picture pasted from the clipboard lands in the body and reaches the recipient as an inline
 * attachment the body refers to, checked at the receiving server.
 */
test("a picture pasted into a message is shown there and arrives inline", async () => {
	await clearInbox(imapPort);
	await fetch(`${mailpitApi}/messages`, { method: "DELETE" });
	const subject = `Pasted ${Date.now()}`;

	const { app, window } = await launchApp();
	try {
		await createImapAccount(window, imapPort);
		await expect(
			window.getByRole("button", { name: "New message" }),
		).toBeEnabled({ timeout: 60_000 });
		await window.getByRole("button", { name: "New message" }).click();
		await window.getByLabel("To", { exact: true }).fill("someone@example.org");
		await window.getByLabel("Subject").fill(subject);

		const message = window.getByLabel("Message", { exact: true });
		await message.click();
		await message.evaluate((element, base64) => {
			const bytes = Uint8Array.from(atob(base64), (c) => c.charCodeAt(0));
			const transfer = new DataTransfer();
			transfer.items.add(new File([bytes], "image.png", { type: "image/png" }));
			element.dispatchEvent(
				new ClipboardEvent("paste", {
					clipboardData: transfer,
					bubbles: true,
					cancelable: true,
				}),
			);
		}, pixel);

		await expect(message.locator("img")).toHaveCount(1, { timeout: 15_000 });
		await expect(message.locator("img")).toHaveAttribute("src", /^blob:/);
		// An inline picture is not listed as an attached file.
		await expect(window.getByLabel("Attached files")).toHaveCount(0);

		// Reopened from the Drafts folder, the picture is still there to see.
		await window.getByRole("button", { name: "Save draft" }).click();
		await expect(window.getByText(/Draft saved at/)).toBeVisible();
		await window
			.getByRole("button", { name: /^Drafts/ })
			.first()
			.click();
		await window
			.getByRole("button", { name: new RegExp(subject) })
			.first()
			.click();
		await expect(window.getByLabel("Subject")).toHaveValue(subject);
		await expect(message.locator("img")).toHaveAttribute("src", /^blob:/, {
			timeout: 15_000,
		});

		await window.getByRole("button", { name: "Send", exact: true }).click();
		await expect
			.poll(
				async () => {
					const response = await fetch(`${mailpitApi}/messages`);
					const body = (await response.json()) as {
						messages: { Subject: string }[];
					};
					return body.messages.filter((m) => m.Subject === subject).length;
				},
				{ timeout: 90_000, message: "the message never reached the server" },
			)
			.toBe(1);

		const list = (await (await fetch(`${mailpitApi}/messages`)).json()) as {
			messages: { ID: string; Subject: string }[];
		};
		const id = list.messages.find((m) => m.Subject === subject)!.ID;
		const received = (await (
			await fetch(`${mailpitApi}/message/${id}`)
		).json()) as {
			HTML: string;
			Inline: { ContentID: string; ContentType: string }[];
		};
		expect(received.Inline).toHaveLength(1);
		expect(received.Inline[0]!.ContentType).toBe("image/png");
		expect(received.HTML).toContain(`cid:${received.Inline[0]!.ContentID}`);
	} finally {
		await app.close();
	}
});
