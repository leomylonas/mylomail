import { connect } from "node:net";

/**
 * Appends a message to the matrix server's INBOX over a raw IMAP socket.
 *
 * Written directly rather than pulled from a library because the test needs exactly one
 * command: a dependency whose whole job is APPEND would be more code to understand, not less.
 */
export async function appendMessage(
	port: number,
	subject: string,
	from = "sender@example.org",
): Promise<void> {
	const message = [
		`From: Someone <${from}>`,
		"To: test@mylomail.local",
		`Subject: ${subject}`,
		`Message-ID: <${subject.replace(/\W+/g, "-")}@example.org>`,
		`Date: ${new Date().toUTCString()}`,
		"",
		`Body of ${subject}.`,
		"",
	].join("\r\n");

	await session(port, async (send, sendLiteral) => {
		await send("a1 LOGIN test@mylomail.local password");
		await sendLiteral(
			"a2",
			`a2 APPEND INBOX {${Buffer.byteLength(message)}}`,
			message,
		);
		await send("a3 LOGOUT");
	});
}

/**
 * Appends a multipart HTML message carrying an inline image, a tracking pixel and a script.
 *
 * Deliberately hostile: the reading pane's job is to render the first and refuse the other
 * two, and a benign message proves none of that (§13).
 */
export async function appendHostileHtmlMessage(
	port: number,
	subject: string,
): Promise<void> {
	// A one-pixel PNG, so the inline part is a real image rather than something the browser
	// silently discards.
	const png =
		"iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==";

	const message = [
		"From: Someone <sender@example.org>",
		"To: test@mylomail.local",
		`Subject: ${subject}`,
		`Message-ID: <${subject.replace(/\W+/g, "-")}@example.org>`,
		`Date: ${new Date().toUTCString()}`,
		"MIME-Version: 1.0",
		'Content-Type: multipart/related; boundary="boundary42"',
		"",
		"--boundary42",
		'Content-Type: text/html; charset="utf-8"',
		"",
		"<html><body>",
		"<p id='visible-body'>Hostile body text</p>",
		`<script>window.pwned = true;</${"script"}>`,
		'<img id="inline" src="cid:inline-image@example.org">',
		'<img id="tracker" src="https://tracker.invalid/pixel.gif">',
		"</body></html>",
		"",
		"--boundary42",
		"Content-Type: image/png",
		"Content-Transfer-Encoding: base64",
		"Content-ID: <inline-image@example.org>",
		"",
		png,
		"",
		"--boundary42--",
		"",
	].join("\r\n");

	await session(port, async (send, sendLiteral) => {
		await send("h1 LOGIN test@mylomail.local password");
		await sendLiteral(
			"h2",
			`h2 APPEND INBOX {${Buffer.byteLength(message)}}`,
			message,
		);
		await send("h3 LOGOUT");
	});
}

/** Removes every message from INBOX, so a test starts from a known mailbox. */
export async function clearInbox(port: number): Promise<void> {
	await clearFolder(port, "INBOX");
}

/**
 * Empties a folder so a spec starts from a known server state.
 *
 * Drafts in particular accumulate across runs, and a leftover draft is not inert: it is
 * ingested like any other message, so it participates in identity matching. One did exactly
 * that and rewrote a seeded inbox message's subject.
 */
export async function clearFolder(port: number, folder: string): Promise<void> {
	await session(port, async (send) => {
		await send("b1 LOGIN test@mylomail.local password");
		await send(`b2 SELECT ${folder}`);
		await send("b3 STORE 1:* +FLAGS (\\Deleted)");
		await send("b4 EXPUNGE");
		await send("b5 LOGOUT");
	});
}

/**
 * The folders the server holds, as it lists them.
 *
 * Asking the server rather than the app is the point: the app's sidebar is drawn from its own
 * database, so it would show a folder it merely believes it created.
 */
export async function foldersOn(port: number): Promise<string[]> {
	const names: string[] = [];

	await session(port, async (send) => {
		await send("f1 LOGIN test@mylomail.local password");
		const listing = await send('f2 LIST "" "*"');
		await send("f3 LOGOUT");

		for (const line of listing.split("\r\n")) {
			const match = /^\* LIST \([^)]*\) "[^"]*" "?([^"]+)"?$/.exec(line.trim());
			if (match) names.push(match[1]);
		}
	});

	return names;
}

/** The full text of every message in a named folder, as the server sees them. */
export async function bodiesIn(port: number, folder: string): Promise<string> {
	const lines: string[] = [];
	await session(port, async (send) => {
		await send("e1 LOGIN test@mylomail.local password");
		await send(`e2 SELECT "${folder}"`);
		lines.push(await send("e3 FETCH 1:* (BODY.PEEK[])"));
		await send("e4 LOGOUT");
	});

	return lines.join("\n");
}

/**
 * How many messages in a folder have this exact subject.
 *
 * Uses SEARCH rather than parsing a FETCH: a FETCH response spans many lines per message and
 * scraping it under-reported silently, which made a duplicate-detection assertion pass while
 * the server actually held two copies. SEARCH answers on one line.
 */
export async function countWithSubject(
	port: number,
	folder: string,
	subject: string,
): Promise<number> {
	let response = "";
	await session(port, async (send) => {
		await send("d1 LOGIN test@mylomail.local password");
		await send(`d2 SELECT "${folder}"`);
		response = await send(`d3 SEARCH HEADER SUBJECT "${subject}"`);
		await send("d4 LOGOUT");
	});

	const line = response
		.split(/\r?\n/)
		.find((candidate) => candidate.toUpperCase().startsWith("* SEARCH"));

	return line ? line.trim().split(/\s+/).slice(2).filter(Boolean).length : 0;
}

/** Reads the flags of every message in INBOX, as the server sees them. */
export async function inboxFlags(port: number): Promise<string[]> {
	const lines: string[] = [];
	await session(port, async (send) => {
		await send("c1 LOGIN test@mylomail.local password");
		await send("c2 SELECT INBOX");
		lines.push(await send("c3 FETCH 1:* (FLAGS)"));
		await send("c4 LOGOUT");
	});

	return lines
		.join("\n")
		.split(/\r?\n/)
		.filter((line) => line.includes("FLAGS"));
}

type Send = (command: string) => Promise<string>;

/** Sends a command that ends in a literal, then the literal itself. */
type SendLiteral = (
	tag: string,
	command: string,
	payload: string,
) => Promise<string>;

async function session(
	port: number,
	run: (send: Send, sendLiteral: SendLiteral) => Promise<void>,
): Promise<void> {
	const socket = connect({ host: "127.0.0.1", port });
	let buffer = "";
	socket.setEncoding("utf8");
	socket.on("data", (chunk: string) => {
		buffer += chunk;
	});

	const settled = (tag: string, expect: "tagged" | "continuation") =>
		new Promise<string>((resolve, reject) => {
			const deadline = Date.now() + 10_000;
			const poll = setInterval(() => {
				const done =
					expect === "continuation"
						? buffer.includes("+ ")
						: new RegExp(`^${tag} (OK|NO|BAD)`, "m").test(buffer);
				if (done) {
					clearInterval(poll);
					const seen = buffer;
					buffer = "";
					resolve(seen);
				} else if (Date.now() > deadline) {
					clearInterval(poll);
					reject(new Error(`IMAP command '${tag}' timed out. Saw: ${buffer}`));
				}
			}, 25);
		});

	await new Promise<void>((resolve, reject) => {
		socket.once("connect", () => resolve());
		socket.once("error", reject);
	});
	buffer = "";

	const send: Send = async (command) => {
		const tag = command.split(" ")[0];
		socket.write(`${command}\r\n`);
		return settled(tag, "tagged");
	};

	// The payload is not a command and has no tag of its own: the server answers the literal
	// announcement with a bare "+", and the original tag only completes after the payload.
	const sendLiteral: SendLiteral = async (tag, command, payload) => {
		socket.write(`${command}\r\n`);
		await settled(tag, "continuation");
		socket.write(`${payload}\r\n`);
		return settled(tag, "tagged");
	};

	try {
		await run(send, sendLiteral);
	} finally {
		socket.destroy();
	}
}
