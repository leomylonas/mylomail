import { quoteSeparator } from "@mylomail/renderer/Components/Compose/ComposeReplyForward";
import { signatureIdentityAttribute } from "@mylomail/renderer/Components/Editor/SignatureNode";

export interface SignatureIdentity {
	id: string;
	signatureHtml?: string | null;
}

/**
 * Replaces the compose-managed signature and leaves every other part of the editable message
 * untouched. The marker is preserved by `SignatureNode`; without it, matching raw HTML could
 * delete ordinary user text that happens to resemble a signature.
 */
export function applyIdentitySignature(
	bodyHtml: string,
	identity: SignatureIdentity,
): string {
	const document = new DOMParser().parseFromString(bodyHtml, "text/html");
	for (const signature of document.body.querySelectorAll(
		`[${signatureIdentityAttribute}]`,
	)) {
		removeManagedSpacer(signature);
		signature.remove();
	}

	if (!identity.signatureHtml) return document.body.innerHTML;

	const spacer = document.createElement("p");
	spacer.append(document.createElement("br"));
	const signature = document.createElement("div");
	signature.setAttribute(signatureIdentityAttribute, identity.id);
	signature.innerHTML = signatureMarkup(identity.signatureHtml);
	// Above the rule that starts a quoted reply or forward, as Outlook puts it; at the end otherwise.
	const rule = [...document.body.children].find(
		(element) =>
			element.tagName === "P" && element.textContent === quoteSeparator,
	);
	if (rule) rule.before(spacer, signature);
	else document.body.append(spacer, signature);
	return document.body.innerHTML;
}

function removeManagedSpacer(signature: Element): void {
	const spacer = signature.previousElementSibling;
	if (
		spacer?.tagName === "P" &&
		spacer.textContent === "" &&
		spacer.children.length === 1 &&
		spacer.firstElementChild?.tagName === "BR"
	) {
		spacer.remove();
	}
}

/**
 * Signatures are typed into a plain text box, so one without any markup is text, not HTML: its
 * line breaks are the author's, and HTML would fold them into a single line.
 */
export function signatureMarkup(signature: string): string {
	if (/<[a-z][^>]*>/i.test(signature)) return signature;
	const container = document.createElement("div");
	signature
		.replace(/\r\n?/g, "\n")
		.split("\n")
		.forEach((line, index) => {
			if (index > 0) container.append(document.createElement("br"));
			container.append(document.createTextNode(line));
		});
	return container.innerHTML;
}
