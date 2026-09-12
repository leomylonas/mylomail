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
	signature.innerHTML = identity.signatureHtml;
	document.body.append(spacer, signature);
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
