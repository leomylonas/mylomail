export interface MailtoComposeRequest {
	to: string[];
	cc: string[];
	bcc: string[];
	subject: string;
	body: string;
}

const maximumMailtoLength = 64 * 1024;

/** Returns every valid mailto activation carried by a process launch. */
export function extractMailtoUris(args: readonly string[]): string[] {
	return args.filter((argument) => parseMailtoUri(argument) !== null);
}

/**
 * Parses the RFC 6068 fields MyloMail can represent in Compose.
 *
 * Header names are case-insensitive. Unknown headers are ignored rather than reflected into
 * message HTML, and recipient/header newlines are removed so an OS activation cannot smuggle a
 * second MIME header into a later send. The body remains multiline and is escaped by the
 * renderer before it becomes HTML.
 */
export function parseMailtoUri(uri: string): MailtoComposeRequest | null {
	if (uri.length > maximumMailtoLength) return null;

	let parsed: URL;
	try {
		parsed = new URL(uri);
	} catch {
		return null;
	}
	if (parsed.protocol !== "mailto:") return null;

	const fields = new Map<string, string[]>();
	for (const [name, value] of parsed.searchParams) {
		const lowered = name.toLowerCase();
		const current = fields.get(lowered);
		if (current) current.push(value);
		else fields.set(lowered, [value]);
	}

	let pathRecipients: string;
	try {
		pathRecipients = decodeURIComponent(parsed.pathname);
	} catch {
		return null;
	}

	return {
		to: uniqueRecipients([pathRecipients, ...(fields.get("to") ?? [])]),
		cc: uniqueRecipients(fields.get("cc") ?? []),
		bcc: uniqueRecipients(fields.get("bcc") ?? []),
		subject: singleLine(fields.get("subject")?.at(-1) ?? ""),
		body: fields.get("body")?.at(-1) ?? "",
	};
}

/** Encodes an activation as an internal same-origin shell-window route. */
export function mailtoWindowQuery(uri: string): string {
	return new URLSearchParams({ mailto: uri }).toString();
}

function uniqueRecipients(values: readonly string[]): string[] {
	const seen = new Set<string>();
	const recipients: string[] = [];
	for (const value of values) {
		for (const part of value.split(",")) {
			const recipient = singleLine(part).trim();
			const key = recipient.toLocaleLowerCase("en-US");
			if (!recipient || seen.has(key)) continue;
			seen.add(key);
			recipients.push(recipient);
		}
	}
	return recipients;
}

function singleLine(value: string): string {
	return value.replace(/[\r\n]+/g, " ");
}
