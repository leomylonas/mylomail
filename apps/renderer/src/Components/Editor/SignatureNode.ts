import {
	$applyNodeReplacement,
	$getDocument,
	ElementNode,
	type DOMConversionMap,
	type DOMConversionOutput,
	type DOMExportOutput,
	type LexicalEditor,
	type NodeKey,
	type SerializedElementNode,
} from "lexical";

export const signatureIdentityAttribute = "data-mylomail-signature";

interface SerializedSignatureNode extends SerializedElementNode {
	identityId: string;
	type: "signature";
	version: 1;
}

/**
 * Editable signature boundary retained in the draft HTML.
 *
 * A plain div loses its attributes when Lexical imports and exports HTML. Keeping the selected
 * identity on a real node lets Compose replace precisely the signature it inserted without
 * guessing from user-authored text or deleting an edited message tail.
 */
export class SignatureNode extends ElementNode {
	__identityId: string;

	static getType(): string {
		return "signature";
	}

	static clone(node: SignatureNode): SignatureNode {
		return new SignatureNode(node.__identityId, node.__key);
	}

	static importDOM(): DOMConversionMap | null {
		return {
			div: (node) =>
				node instanceof HTMLElement &&
				node.hasAttribute(signatureIdentityAttribute)
					? { conversion: convertSignatureElement, priority: 2 }
					: null,
		};
	}

	static importJSON(serializedNode: SerializedElementNode): SignatureNode {
		const identityId =
			"identityId" in serializedNode &&
			typeof serializedNode.identityId === "string"
				? serializedNode.identityId
				: "";
		return $createSignatureNode(identityId).updateFromJSON(serializedNode);
	}

	constructor(identityId: string, key?: NodeKey) {
		super(key);
		this.__identityId = identityId;
	}

	createDOM(): HTMLElement {
		const element = $getDocument().createElement("div");
		element.setAttribute(signatureIdentityAttribute, this.__identityId);
		return element;
	}

	updateDOM(previous: SignatureNode, element: HTMLElement): boolean {
		if (previous.__identityId !== this.__identityId) {
			element.setAttribute(signatureIdentityAttribute, this.__identityId);
		}
		return false;
	}

	exportDOM(editor: LexicalEditor): DOMExportOutput {
		const output = super.exportDOM(editor);
		if (output.element instanceof HTMLElement) {
			output.element.setAttribute(
				signatureIdentityAttribute,
				this.__identityId,
			);
		}
		return output;
	}

	exportJSON(): SerializedSignatureNode {
		return {
			...super.exportJSON(),
			identityId: this.__identityId,
			type: "signature",
			version: 1,
		};
	}
}

function convertSignatureElement(element: HTMLElement): DOMConversionOutput {
	const identityId = element.getAttribute(signatureIdentityAttribute);
	return { node: identityId ? $createSignatureNode(identityId) : null };
}

function $createSignatureNode(identityId: string): SignatureNode {
	return $applyNodeReplacement(new SignatureNode(identityId));
}
