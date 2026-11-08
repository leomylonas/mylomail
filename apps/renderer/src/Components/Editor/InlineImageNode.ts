import {
	$applyNodeReplacement,
	DecoratorNode,
	type DOMConversionMap,
	type DOMConversionOutput,
	type DOMExportOutput,
	type LexicalNode,
	type NodeKey,
	type SerializedLexicalNode,
	type Spread,
} from "lexical";

type SerializedInlineImageNode = Spread<
	{ contentId: string; alt: string; type: "inline-image"; version: 1 },
	SerializedLexicalNode
>;

/**
 * An image in the body that travels with the message as an inline attachment.
 *
 * The saved HTML refers to it as `cid:<contentId>`, which is what a mail client resolves against
 * the attachment of that Content-ID. The editor cannot show a `cid:` address, so the node also
 * holds a `source` it can show (a local object URL), which is never written out.
 */
export class InlineImageNode extends DecoratorNode<null> {
	__contentId: string;
	__alt: string;
	/** Where the editor reads the picture from; empty until it has been fetched. */
	__source: string;

	static getType(): string {
		return "inline-image";
	}

	static clone(node: InlineImageNode): InlineImageNode {
		return new InlineImageNode(
			node.__contentId,
			node.__alt,
			node.__source,
			node.__key,
		);
	}

	static importDOM(): DOMConversionMap | null {
		return {
			img: (node) =>
				node instanceof HTMLImageElement &&
				node.getAttribute("src")?.toLowerCase().startsWith("cid:")
					? { conversion: convertImageElement, priority: 1 }
					: null,
		};
	}

	static importJSON(serialized: SerializedInlineImageNode): InlineImageNode {
		return $createInlineImageNode(serialized.contentId, serialized.alt);
	}

	constructor(contentId: string, alt = "", source = "", key?: NodeKey) {
		super(key);
		this.__contentId = contentId;
		this.__alt = alt;
		this.__source = source;
	}

	exportJSON(): SerializedInlineImageNode {
		return {
			...super.exportJSON(),
			type: "inline-image",
			version: 1,
			contentId: this.__contentId,
			alt: this.__alt,
		};
	}

	getContentId(): string {
		return this.getLatest().__contentId;
	}

	getSource(): string {
		return this.getLatest().__source;
	}

	setSource(source: string): this {
		const writable = this.getWritable();
		writable.__source = source;
		return writable;
	}

	createDOM(): HTMLElement {
		const image = document.createElement("img");
		image.alt = this.__alt;
		if (this.__source) image.src = this.__source;
		image.draggable = false;
		return image;
	}

	updateDOM(prev: InlineImageNode, dom: HTMLElement): boolean {
		if (prev.__source !== this.__source && dom instanceof HTMLImageElement) {
			dom.src = this.__source;
		}
		return false;
	}

	exportDOM(): DOMExportOutput {
		const image = document.createElement("img");
		image.setAttribute("src", `cid:${this.__contentId}`);
		image.setAttribute("alt", this.__alt);
		return { element: image };
	}

	isInline(): boolean {
		return true;
	}

	decorate(): null {
		return null;
	}
}

function convertImageElement(node: Node): DOMConversionOutput {
	const image = node as HTMLImageElement;
	const contentId = (image.getAttribute("src") ?? "").slice("cid:".length);
	return {
		node: $createInlineImageNode(contentId, image.getAttribute("alt") ?? ""),
	};
}

export function $createInlineImageNode(
	contentId: string,
	alt = "",
	source = "",
): InlineImageNode {
	return $applyNodeReplacement(new InlineImageNode(contentId, alt, source));
}

export function $isInlineImageNode(
	node: LexicalNode | null | undefined,
): node is InlineImageNode {
	return node instanceof InlineImageNode;
}
