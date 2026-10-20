import { useCallback, useState } from "react";
import { $generateHtmlFromNodes, $generateNodesFromDOM } from "@lexical/html";
import { LinkNode, TOGGLE_LINK_COMMAND } from "@lexical/link";
import {
	INSERT_ORDERED_LIST_COMMAND,
	INSERT_UNORDERED_LIST_COMMAND,
	ListItemNode,
	ListNode,
} from "@lexical/list";
import { LexicalComposer } from "@lexical/react/LexicalComposer";
import { ContentEditable } from "@lexical/react/LexicalContentEditable";
import { LexicalErrorBoundary } from "@lexical/react/LexicalErrorBoundary";
import { HistoryPlugin } from "@lexical/react/LexicalHistoryPlugin";
import { LinkPlugin } from "@lexical/react/LexicalLinkPlugin";
import { ListPlugin } from "@lexical/react/LexicalListPlugin";
import { OnChangePlugin } from "@lexical/react/LexicalOnChangePlugin";
import { RichTextPlugin } from "@lexical/react/LexicalRichTextPlugin";
import { useLexicalComposerContext } from "@lexical/react/LexicalComposerContext";
import { HeadingNode, QuoteNode } from "@lexical/rich-text";
import { Button, IconButton, TextInput } from "@carbon/react";
import {
	type CarbonIconType,
	Link,
	ListBulleted,
	ListNumbered,
	Redo,
	TextBold,
	TextItalic,
	TextStrikethrough,
	TextUnderline,
	Undo,
} from "@carbon/icons-react";
import {
	FORMAT_TEXT_COMMAND,
	REDO_COMMAND,
	UNDO_COMMAND,
	TextNode,
	$getRoot,
	$insertNodes,
	$isTextNode,
	type DOMExportOutputMap,
	type EditorState,
	type LexicalEditor,
} from "lexical";
import { SignatureNode } from "@mylomail/renderer/Components/Editor/SignatureNode";
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
					text: { bold: styles.bold, italic: styles.italic },
				},
				html: { export: composeHtmlExport },
			}}
		>
			<div className={styles.shell}>
				<Toolbar />
				<div className={styles.surface}>
					<RichTextPlugin
						contentEditable={
							<ContentEditable className={styles.input} aria-label="Message" />
						}
						placeholder={
							<div className={styles.placeholder}>{placeholder}</div>
						}
						ErrorBoundary={LexicalErrorBoundary}
					/>
				</div>
			</div>
			<HistoryPlugin />
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

/** Inline and list formatting plus links and undo: what mail needs, not a word processor. */
function Toolbar() {
	const [editor] = useLexicalComposerContext();
	const [linkUrl, setLinkUrl] = useState<string | null>(null);

	const format = (label: string, Icon: CarbonIconType, run: () => void) => (
		<IconButton
			size="sm"
			kind="ghost"
			label={label}
			onClick={run}
			// Keep the selection in the editor when the button is pressed.
			onMouseDown={(event) => event.preventDefault()}
		>
			<Icon />
		</IconButton>
	);

	const text =
		(name: "bold" | "italic" | "underline" | "strikethrough") => () =>
			editor.dispatchCommand(FORMAT_TEXT_COMMAND, name);

	const applyLink = () => {
		const url = linkUrl?.trim();
		editor.dispatchCommand(
			TOGGLE_LINK_COMMAND,
			url ? (/^[a-z][a-z0-9+.-]*:/i.test(url) ? url : `https://${url}`) : null,
		);
		setLinkUrl(null);
		editor.focus();
	};

	return (
		<div className={styles.toolbar} role="toolbar" aria-label="Formatting">
			{format("Bold", TextBold, text("bold"))}
			{format("Italic", TextItalic, text("italic"))}
			{format("Underline", TextUnderline, text("underline"))}
			{format("Strikethrough", TextStrikethrough, text("strikethrough"))}
			<span className={styles.separator} />
			{format("Bulleted list", ListBulleted, () =>
				editor.dispatchCommand(INSERT_UNORDERED_LIST_COMMAND, undefined),
			)}
			{format("Numbered list", ListNumbered, () =>
				editor.dispatchCommand(INSERT_ORDERED_LIST_COMMAND, undefined),
			)}
			<span className={styles.separator} />
			{format("Link", Link, () => setLinkUrl(linkUrl === null ? "" : null))}
			<span className={styles.separator} />
			{format("Undo", Undo, () =>
				editor.dispatchCommand(UNDO_COMMAND, undefined),
			)}
			{format("Redo", Redo, () =>
				editor.dispatchCommand(REDO_COMMAND, undefined),
			)}
			{linkUrl === null ? null : (
				<form
					className={styles.linkForm}
					onSubmit={(event) => {
						event.preventDefault();
						applyLink();
					}}
				>
					<TextInput
						id="compose-link-url"
						size="sm"
						labelText="Link address"
						hideLabel
						placeholder="Link address; empty removes the link"
						value={linkUrl}
						onChange={(event) => setLinkUrl(event.target.value)}
						ref={(input: HTMLInputElement | null) => {
							if (input && document.activeElement !== input) input.focus();
						}}
					/>
					<Button size="sm" type="submit">
						Apply
					</Button>
				</form>
			)}
		</div>
	);
}
