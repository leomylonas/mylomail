import { useCallback, useEffect, useRef } from "react";
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
	PASTE_COMMAND,
	$nodesOfType,
	$getNodeByKey,
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
import {
	$createInlineImageNode,
	$isInlineImageNode,
	InlineImageNode,
} from "@mylomail/renderer/Components/Editor/InlineImageNode";
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
	onPasteImage,
	loadInlineImage,
}: {
	onChange: (html: string) => void;
	placeholder?: string;
	initialHtml?: string;
	/**
	 * Stores a picture pasted from the clipboard as an inline attachment and returns its
	 * Content-ID, or null when it could not be kept (the paste is then dropped, not half-made).
	 */
	onPasteImage?: (image: File) => Promise<string | null>;
	/** Fetches an inline image already on the draft, as an address the editor can show. */
	loadInlineImage?: (contentId: string) => Promise<string | null>;
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
					InlineImageNode,
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
			<InlineImagePlugin
				onPasteImage={onPasteImage}
				loadInlineImage={loadInlineImage}
			/>
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

/**
 * Pasting a picture from the clipboard puts it in the message where the caret is.
 *
 * The picture is handed to `onPasteImage` first, to become an inline attachment; only then does it
 * appear in the body, so the body never refers to something that was not kept. Pictures the draft
 * already holds (reopened, replied to) are fetched once so the editor can show them.
 */
function InlineImagePlugin({
	onPasteImage,
	loadInlineImage,
}: {
	onPasteImage?: (image: File) => Promise<string | null>;
	loadInlineImage?: (contentId: string) => Promise<string | null>;
}) {
	const [editor] = useLexicalComposerContext();
	const sources = useRef(new Map<string, Promise<string | null>>());

	const sourceFor = useCallback(
		(contentId: string, local?: string): Promise<string | null> => {
			let found = sources.current.get(contentId);
			if (!found) {
				found = local
					? Promise.resolve(local)
					: (loadInlineImage?.(contentId) ?? Promise.resolve(null));
				sources.current.set(contentId, found);
			}
			return found;
		},
		[loadInlineImage],
	);

	useEffect(() => {
		const showWhenLoaded = (contentId: string, local?: string) => {
			void sourceFor(contentId, local).then((source) => {
				if (!source) return;
				editor.update(() => {
					for (const node of $nodesOfType(InlineImageNode)) {
						if (node.getContentId() === contentId && !node.getSource()) {
							node.setSource(source);
						}
					}
				});
			});
		};

		const stopWatching = editor.registerMutationListener(
			InlineImageNode,
			(mutations) => {
				for (const [key, mutation] of mutations) {
					if (mutation === "destroyed") continue;
					const contentId = editor.read(() => {
						const node = $getNodeByKey(key);
						return $isInlineImageNode(node) && !node.getSource()
							? node.getContentId()
							: null;
					});
					if (contentId) showWhenLoaded(contentId);
				}
			},
			{ skipInitialization: false },
		);

		const stopPasting = editor.registerCommand(
			PASTE_COMMAND,
			(event) => {
				const files =
					event instanceof ClipboardEvent
						? Array.from(event.clipboardData?.files ?? []).filter((file) =>
								file.type.startsWith("image/"),
							)
						: [];
				if (files.length === 0 || !onPasteImage) return false;
				event.preventDefault();
				void (async () => {
					for (const file of files) {
						const contentId = await onPasteImage(file);
						if (!contentId) continue;
						const local = URL.createObjectURL(file);
						sources.current.set(contentId, Promise.resolve(local));
						editor.update(() => {
							const selection = $getSelection();
							if (!$isRangeSelection(selection)) $getRoot().selectEnd();
							$insertNodes([$createInlineImageNode(contentId, "", local)]);
						});
					}
				})();
				return true;
			},
			COMMAND_PRIORITY_LOW,
		);
		return () => {
			stopWatching();
			stopPasting();
		};
	}, [editor, onPasteImage, sourceFor]);

	return null;
}
