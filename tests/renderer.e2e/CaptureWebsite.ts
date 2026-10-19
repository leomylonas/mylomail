import { execFileSync } from "node:child_process";
import { randomUUID } from "node:crypto";
import { mkdirSync, rmSync, writeFileSync } from "node:fs";
import { dirname, join, resolve } from "node:path";
import { expect } from "@playwright/test";
import { launchApp, type LaunchedApp } from "@mylomail/renderer-e2e/AppFixture";
import { createImapAccount } from "@mylomail/renderer-e2e/SeedAccount";
import { appendRawMessage } from "@mylomail/renderer-e2e/SeedImap";

if (process.platform !== "linux") {
	throw new Error(
		"Website screenshots use the Linux-isolated desktop test fixtures.",
	);
}

const repository = resolve(import.meta.dirname, "../..");
const destination = join(repository, "website", "public", "Screenshots");
const session = randomUUID().slice(0, 8);
const containers: string[] = [];
let launched: LaunchedApp | undefined;

type DemoMessage = {
	from: string;
	subject: string;
	body: string;
	attachment?: string;
};

const accounts = [
	{
		name: "Studio",
		email: "alex@studio.example",
		messages: [
			{
				from: "Mara Chen <mara@aster.example>",
				subject: "Thursday's workshop",
				body: "Hi Alex,\n\nEverything is ready for Thursday's workshop. We will meet at the studio at 10:00, then review the new layouts together.\n\nThe plan for the morning:\n\n10:00  Coffee and introductions\n10:30  Design review\n11:30  Next steps\n\nBring your notebook; the materials will be waiting.\n\nSee you Thursday,\nMara",
				attachment:
					"Workshop agenda\n\n10:00 Coffee and introductions\n10:30 Design review\n11:30 Next steps\n",
			},
			{
				from: "Rowan Ellis <rowan@north.example>",
				subject: "Updated floor plans",
				body: "The new floor plans are ready for review. The shared work area now has a little more room for the whole team.",
			},
			{
				from: "Aster Studio <team@aster.example>",
				subject: "Notes from the design review",
				body: "Thanks for a thoughtful review yesterday. Here are the decisions and next steps for our next round of layouts.",
			},
			{
				from: "Nadia Park <nadia@paper.example>",
				subject: "Paper samples have arrived",
				body: "The sample pack is here. I have put it by the window so we can compare the colours in daylight.",
			},
			{
				from: "Sam Rivera <sam@fieldwork.example>",
				subject: "A few ideas for the courtyard",
				body: "A few ideas from this morning's walk: more places to sit, a patch of shade, and room for the plants to grow.",
			},
			{
				from: "The Reading Room <hello@reading.example>",
				subject: "Your October reading list",
				body: "This month's selection is about making thoughtful spaces, finding a useful rhythm, and looking a little closer.",
			},
			{
				from: "Jules Morgan <jules@studio.example>",
				subject: "Friday afternoon catch-up",
				body: "Shall we take half an hour on Friday to look back at the week and plan what comes next?",
			},
			{
				from: "Mara Chen <mara@aster.example>",
				subject: "Welcome to the studio",
				body: "A quick hello from everyone at Aster. We are glad you are here and look forward to working together.",
			},
		],
	},
	{
		name: "Personal",
		email: "alex@personal.example",
		messages: [
			{
				from: "Jamie Lee <jamie@friends.example>",
				subject: "Coffee on Saturday?",
				body: "The little cafe by the park is open again. Would you like to meet there on Saturday morning?",
			},
			{
				from: "The Garden Club <hello@garden.example>",
				subject: "A good weekend for planting",
				body: "There is still time to plant a few things before the weather changes. Here are this week's notes from the garden.",
			},
			{
				from: "Riley <riley@friends.example>",
				subject: "Photos from the coast",
				body: "A few favourite moments from our trip to the coast. It was good to get outside together.",
			},
		],
	},
	{
		name: "Community",
		email: "alex@community.example",
		messages: [
			{
				from: "Neighbourhood Library <team@library.example>",
				subject: "Next month's reading circle",
				body: "Our next reading circle is on the first Thursday of the month. Everyone is welcome to bring a friend.",
			},
			{
				from: "Community Garden <team@community.example>",
				subject: "The autumn working bee",
				body: "We will be meeting in the garden on Sunday to tidy the paths and prepare the new beds.",
			},
		],
	},
];

function docker(args: string[]): string {
	return execFileSync("docker", args, {
		cwd: repository,
		encoding: "utf8",
		timeout: 60_000,
		stdio: ["pipe", "pipe", "pipe"],
	}).trim();
}

function startContainer(name: string, args: string[]): void {
	docker(["run", "--detach", "--rm", "--name", name, ...args]);
	containers.push(name);
}

function publishedPort(name: string, port: number): number {
	const ports = JSON.parse(
		docker(["inspect", "--format", "{{json .NetworkSettings.Ports}}", name]),
	) as Record<string, { HostPort: string }[]>;
	return Number(ports[`${port}/tcp`][0].HostPort);
}

function messageMime(
	message: DemoMessage,
	to: string,
	index: number,
	date: Date,
): string {
	const boundary = `website-${index}`;
	const headers = [
		`From: ${message.from}`,
		`To: Alex <${to}>`,
		`Subject: ${message.subject}`,
		`Date: ${date.toUTCString()}`,
		`Message-ID: <website-${index}-${to}>`,
		"MIME-Version: 1.0",
	];
	const text = [
		"Content-Type: text/plain; charset=utf-8",
		"Content-Transfer-Encoding: 8bit",
		"",
		message.body,
	];
	if (!message.attachment) return [...headers, ...text, ""].join("\r\n");
	const attachment = Buffer.from(message.attachment)
		.toString("base64")
		.match(/.{1,76}/g)!
		.join("\r\n");
	return [
		...headers,
		`Content-Type: multipart/mixed; boundary="${boundary}"`,
		"",
		`--${boundary}`,
		...text,
		`--${boundary}`,
		'Content-Type: text/plain; name="Workshop-agenda.txt"',
		'Content-Disposition: attachment; filename="Workshop-agenda.txt"',
		"Content-Transfer-Encoding: base64",
		"",
		attachment,
		`--${boundary}--`,
		"",
	].join("\r\n");
}

try {
	// Dedicated containers and per-user maildirs never read or reset the shared E2E matrix.
	execFileSync("bash", ["tests/imap-matrix/generate-cert.sh"], {
		cwd: repository,
		stdio: "pipe",
	});
	const imap = `mylomail-website-imap-${session}`;
	const smtp = `mylomail-website-smtp-${session}`;
	const mounts = [
		["conf/Base.conf", "/etc/dovecot/Base.conf"],
		["conf/Tier-QResync.conf", "/etc/dovecot/dovecot.conf"],
		["certs/cert.pem", "/etc/dovecot/certs/server.crt"],
		["certs/key.pem", "/etc/dovecot/private/server.key"],
	].flatMap(([source, target]) => [
		"--mount",
		`type=bind,source=${join(repository, "tests", "imap-matrix", source)},target=${target},readonly`,
	]);
	startContainer(imap, [
		"--publish",
		"127.0.0.1::143",
		"--tmpfs",
		"/srv/mail:uid=1000,gid=1000,mode=0700",
		"--health-cmd",
		"doveadm service status imap-login",
		"--health-interval",
		"1s",
		"--health-timeout",
		"3s",
		"--health-retries",
		"30",
		...mounts,
		"dovecot/dovecot:2.3.21",
	]);
	startContainer(smtp, [
		"--publish",
		"127.0.0.1::1025",
		"--health-cmd",
		"/mailpit readyz",
		"--health-interval",
		"1s",
		"--health-timeout",
		"3s",
		"--health-retries",
		"30",
		"axllent/mailpit:v1.21",
	]);
	for (const name of containers) {
		await expect
			.poll(
				() => docker(["inspect", "--format", "{{.State.Health.Status}}", name]),
				{ timeout: 60_000 },
			)
			.toBe("healthy");
	}
	const imapPort = publishedPort(imap, 143);
	const smtpPort = publishedPort(smtp, 1025);
	const morning = new Date();
	morning.setUTCHours(10, 30, 0, 0);

	for (const account of accounts) {
		for (let index = account.messages.length - 1; index >= 0; index--) {
			const receivedAt = new Date(morning.getTime() - index * 30 * 60_000);
			await appendRawMessage(
				imapPort,
				account.email,
				messageMime(account.messages[index], account.email, index, receivedAt),
				receivedAt,
			);
		}
	}

	launched = await launchApp([], {
		TZ: "UTC",
		LANG: "en_GB.UTF-8",
		LC_ALL: "en_GB.UTF-8",
	});
	const { window } = launched;
	await window.setViewportSize({ width: 1440, height: 900 });
	await expect(
		window.getByRole("heading", { name: "Mylo Mail", exact: true }),
	).toBeVisible();
	for (const account of accounts) {
		await createImapAccount(
			window,
			imapPort,
			undefined,
			{ displayName: account.name, emailAddress: account.email },
			{ userName: account.email, smtpPort },
		);
	}
	const sidebar = window.getByRole("navigation", {
		name: "Accounts and mailboxes",
	});
	const studio = sidebar
		.locator("section")
		.filter({ has: window.getByRole("button", { name: /^Studio\b/ }) });
	await expect(studio).toBeVisible({ timeout: 60_000 });
	await studio.getByRole("button", { name: /^Inbox\b/ }).click();
	for (const message of accounts[0].messages) {
		await expect(
			window.getByRole("button", {
				name: new RegExp(
					message.subject.replace(/[.*+?^${}()|[\]\\]/g, "\\$&"),
				),
			}),
		).toBeVisible({ timeout: 60_000 });
	}
	await window.getByRole("button", { name: /Thursday's workshop/ }).click();
	await expect(
		window.getByRole("article", { name: "Message", exact: true }),
	).toContainText("Bring your notebook; the materials will be waiting.", {
		timeout: 60_000,
	});
	await expect(window.getByText("Up to date", { exact: true })).toBeVisible({
		timeout: 90_000,
	});

	const captures: { name: string; image: Buffer }[] = [];
	for (const theme of ["Light", "Dark"]) {
		await window.getByRole("button", { name: "Settings", exact: true }).click();
		const radio = window.getByRole("radio", { name: theme, exact: true });
		await radio.focus();
		await radio.press("Space");
		await expect(radio).toBeChecked();
		await window
			.getByRole("button", { name: "Close settings", exact: true })
			.click();
		await expect(window.locator("html")).toHaveClass(
			theme === "Light" ? /cds--white/ : /cds--g100/,
		);
		await window.evaluate(async () => {
			await document.fonts.ready;
			await document.querySelector<HTMLImageElement>("header img")?.decode();
		});
		await window.mouse.move(1000, 20);
		captures.push({
			name: `Mail${theme}.png`,
			image: await window.screenshot({ type: "png", animations: "disabled" }),
		});
	}
	mkdirSync(destination, { recursive: true });
	for (const capture of captures)
		writeFileSync(join(destination, capture.name), capture.image);
	console.log(
		"Captured the actual app in light and dark themes with three synthetic accounts.",
	);
} finally {
	try {
		await launched?.app.close();
	} finally {
		if (launched)
			rmSync(dirname(launched.dataDirectory), { recursive: true, force: true });
		for (const name of containers.reverse()) {
			try {
				docker(["rm", "--force", name]);
			} catch {
				console.error(`Could not remove screenshot container ${name}.`);
			}
		}
	}
}
