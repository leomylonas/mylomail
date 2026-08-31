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

/** Removes every message from INBOX, so a test starts from a known mailbox. */
export async function clearInbox(port: number): Promise<void> {
	await session(port, async (send) => {
		await send("b1 LOGIN test@mylomail.local password");
		await send("b2 SELECT INBOX");
		await send("b3 STORE 1:* +FLAGS (\\Deleted)");
		await send("b4 EXPUNGE");
		await send("b5 LOGOUT");
	});
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
