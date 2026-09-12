import { useCallback } from "react";
import { $generateHtmlFromNodes, $generateNodesFromDOM } from "@lexical/html";
import { LinkNode } from "@lexical/link";
import { ListItemNode, ListNode } from "@lexical/list";
import { LexicalComposer } from "@lexical/react/LexicalComposer";
import { ContentEditable } from "@lexical/react/LexicalContentEditable";
import { LexicalErrorBoundary } from "@lexical/react/LexicalErrorBoundary";
import { HistoryPlugin } from "@lexical/react/LexicalHistoryPlugin";
import { OnChangePlugin } from "@lexical/react/LexicalOnChangePlugin";
import { RichTextPlugin } from "@lexical/react/LexicalRichTextPlugin";
import { useLexicalComposerContext } from "@lexical/react/LexicalComposerContext";
import { HeadingNode, QuoteNode } from "@lexical/rich-text";
import { Button } from "@carbon/react";
import {
	FORMAT_TEXT_COMMAND,
	$getRoot,
	$insertNodes,
	type EditorState,
	type LexicalEditor,
} from "lexical";
import { SignatureNode } from "@mylomail/renderer/Components/Editor/SignatureNode";
import styles from "@mylomail/renderer/Components/Editor/Editor.module.css";

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
						}
					: undefined,
				theme: {
					text: { bold: "bold", italic: "italic" },
				},
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

/** A deliberately small formatting set: compose needs bold and italic, not a word processor. */
function Toolbar() {
	const [editor] = useLexicalComposerContext();

	return (
		<div className={styles.toolbar}>
			<Button
				size="sm"
				kind="ghost"
				aria-label="Bold"
				onClick={() => editor.dispatchCommand(FORMAT_TEXT_COMMAND, "bold")}
			>
				Bold
			</Button>
			<Button
				size="sm"
				kind="ghost"
				aria-label="Italic"
				onClick={() => editor.dispatchCommand(FORMAT_TEXT_COMMAND, "italic")}
			>
				Italic
			</Button>
		</div>
	);
}
