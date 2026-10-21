import { useCallback, useEffect } from "react";
import { $generateHtmlFromNodes, $generateNodesFromDOM } from "@lexical/html";
import { LinkNode } from "@lexical/link";
import { ListItemNode, ListNode } from "@lexical/list";
import { useLexicalComposerContext } from "@lexical/react/LexicalComposerContext";
import { LexicalComposer } from "@lexical/react/LexicalComposer";
import { ContentEditable } from "@lexical/react/LexicalContentEditable";
import { LexicalErrorBoundary } from "@lexical/react/LexicalErrorBoundary";
import { HistoryPlugin } from "@lexical/react/LexicalHistoryPlugin";
import { LinkPlugin } from "@lexical/react/LexicalLinkPlugin";
import { ListPlugin } from "@lexical/react/LexicalListPlugin";
import { OnChangePlugin } from "@lexical/react/LexicalOnChangePlugin";
import { RichTextPlugin } from "@lexical/react/LexicalRichTextPlugin";
import { HeadingNode, QuoteNode } from "@lexical/rich-text";
import {
	TextNode,
	$getRoot,
	$getSelection,
	$isRangeSelection,
	COMMAND_PRIORITY_LOW,
	INSERT_PARAGRAPH_COMMAND,
	$insertNodes,
	$isTextNode,
	type DOMExportOutputMap,
	type EditorState,
	type LexicalEditor,
} from "lexical";
import { EditorToolbar } from "@mylomail/renderer/Components/Editor/EditorToolbar/EditorToolbar";
import {
	SignatureNode,
	$isSignatureNode,
} from "@mylomail/renderer/Components/Editor/SignatureNode";
import styles from "@mylomail/renderer/Components/Editor/Editor.module.css";

const composeHtmlExport: DOMExportOutputMap = new Map([
	[
		TextNode,
		(_editor, node) => {
			if (!$isTextNode(node)) return { element: null };

			let text = node.getTextContent();
			if (node.hasFormat("lowercase")) text = text.toLowerCase();
			else if (node.hasFormat("uppercase")) text = text.toUpperCase();

			let element = document.createElement("span");
			element.textContent = text;
			// Property by property: assigning the whole style string counts as setting the style
			// attribute, which the strict CSP refuses even on a detached element.
			for (const declaration of node.getStyle().split(";")) {
				const colon = declaration.indexOf(":");
				if (colon > 0)
					element.style.setProperty(
						declaration.slice(0, colon).trim(),
						declaration.slice(colon + 1).trim(),
					);
			}

			if (node.hasFormat("code")) element = wrapText(element, "code");
			else if (node.hasFormat("highlight")) element = wrapText(element, "mark");
			else if (node.hasFormat("subscript")) element = wrapText(element, "sub");
			else if (node.hasFormat("superscript"))
				element = wrapText(element, "sup");

			if (node.hasFormat("bold")) element = wrapText(element, "strong");
			if (node.hasFormat("italic")) element = wrapText(element, "em");
			if (node.hasFormat("strikethrough")) element = wrapText(element, "s");
			if (node.hasFormat("underline")) element = wrapText(element, "u");

			return { element };
		},
	],
]);

/**
 * Lexical's default HTML exporter assigns `white-space: pre-wrap` to every text node. Merely
 * assigning that style in the renderer violates the strict CSP, even though the temporary
 * export DOM is detached. Compose supports semantic formatting, so emit semantic elements
 * without an inline style rather than weakening `style-src`.
 */
function wrapText<T extends keyof HTMLElementTagNameMap>(
	element: HTMLElement,
	tagName: T,
): HTMLElement {
	const wrapper = document.createElement(tagName);
	wrapper.append(element);
	return wrapper;
}

/**
 * The compose editor (§12).
 *
 * <b>HTML is the stored form, not Lexical's own state.</b> A draft's body is
 * <c>BodyHtml</c> and a sent message is MIME; persisting the editor's internal JSON would
 * make the mail format depend on an editor version, and would leave a draft written today
 * unreadable if the editor were ever replaced.
 */
export function Editor({
	onChange,
	placeholder = "Write your message",
	initialHtml = "",
}: {
	onChange: (html: string) => void;
	placeholder?: string;
	initialHtml?: string;
}) {
	return (
		<LexicalComposer
			initialConfig={{
				namespace: "compose",
				nodes: [
					HeadingNode,
					QuoteNode,
					ListNode,
					ListItemNode,
					LinkNode,
					SignatureNode,
				],
				// A composition failure must not take the window with it: the surrounding
				// compose form still holds the user's recipients and subject.
				onError: (error) => console.error(`editor: ${error.message}`),
				editorState: initialHtml
					? (editor) => {
							const document = new DOMParser().parseFromString(
								initialHtml,
								"text/html",
							);
							const nodes = $generateNodesFromDOM(editor, document);
							$getRoot().select();
							$insertNodes(nodes);
							// Writing starts at the top, above any quoted message or signature,
							// not wherever inserting the content happened to leave the cursor.
							$getRoot().selectStart();
						}
					: undefined,
				theme: {
					text: {
						bold: styles.bold,
						italic: styles.italic,
						underline: styles.underline,
						strikethrough: styles.strikethrough,
						underlineStrikethrough: styles.underlineStrikethrough,
					},
				},
				html: { export: composeHtmlExport },
			}}
		>
			<div className={styles.shell}>
				<EditorToolbar />
				<div className={styles.surface}>
					<RichTextPlugin
						contentEditable={
							<ContentEditable
								className={`${styles.input} messageContent`}
								aria-label="Message"
								tabIndex={0}
							/>
						}
						placeholder={
							<div className={`${styles.placeholder} messageContent`}>
								{placeholder}
							</div>
						}
						ErrorBoundary={LexicalErrorBoundary}
					/>
				</div>
			</div>
			<HistoryPlugin />
			<SignatureLineBreakPlugin />
			<ListPlugin />
			<LinkPlugin />
			<HtmlChangePlugin onChange={onChange} />
		</LexicalComposer>
	);
}

/**
 * Reports the body as HTML on every change.
 *
 * Serialised inside a read of the editor state rather than from a cached copy: Lexical's
 * state is immutable per update, and reading it outside one is how a body one keystroke
 * behind gets saved.
 */
function HtmlChangePlugin({ onChange }: { onChange: (html: string) => void }) {
	const handle = useCallback(
		(_state: EditorState, editor: LexicalEditor) => {
			editor.read(() => onChange($generateHtmlFromNodes(editor, null)));
		},
		[onChange],
	);

	return <OnChangePlugin onChange={handle} ignoreSelectionChange />;
}

/**
 * Enter inside the signature starts a new line of it. The signature is one block, so the default
 * (split the block) has nowhere to put the cursor and the line was never made.
 */
function SignatureLineBreakPlugin() {
	const [editor] = useLexicalComposerContext();
	useEffect(
		() =>
			editor.registerCommand(
				INSERT_PARAGRAPH_COMMAND,
				() => {
					const selection = $getSelection();
					if (
						!$isRangeSelection(selection) ||
						!$isSignatureNode(selection.anchor.getNode().getTopLevelElement())
					)
						return false;
					selection.insertLineBreak();
					return true;
				},
				COMMAND_PRIORITY_LOW,
			),
		[editor],
	);
	return null;
}
