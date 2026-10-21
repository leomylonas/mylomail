import { useEffect, useState } from "react";
import { TOGGLE_LINK_COMMAND } from "@lexical/link";
import {
	INSERT_ORDERED_LIST_COMMAND,
	INSERT_UNORDERED_LIST_COMMAND,
} from "@lexical/list";
import { useLexicalComposerContext } from "@lexical/react/LexicalComposerContext";
import {
	$createHeadingNode,
	$createQuoteNode,
	$isHeadingNode,
	$isQuoteNode,
	type HeadingTagType,
} from "@lexical/rich-text";
import { $patchStyleText, $setBlocksType } from "@lexical/selection";
import {
	Button,
	IconButton,
	Select,
	SelectItem,
	TextInput,
} from "@carbon/react";
import {
	type CarbonIconType,
	Link,
	ListBulleted,
	ListNumbered,
	Redo,
	TextAlignCenter,
	TextAlignJustify,
	TextAlignLeft,
	TextAlignRight,
	TextBold,
	TextClearFormat,
	TextColor,
	TextHighlight,
	TextIndentLess,
	TextIndentMore,
	TextItalic,
	TextStrikethrough,
	TextSubscript,
	TextSuperscript,
	TextUnderline,
	Undo,
	Unlink,
} from "@carbon/icons-react";
import {
	$createParagraphNode,
	$getSelection,
	$isRangeSelection,
	$isTextNode,
	FORMAT_ELEMENT_COMMAND,
	FORMAT_TEXT_COMMAND,
	INDENT_CONTENT_COMMAND,
	OUTDENT_CONTENT_COMMAND,
	REDO_COMMAND,
	SELECTION_CHANGE_COMMAND,
	UNDO_COMMAND,
	COMMAND_PRIORITY_LOW,
	type ElementFormatType,
	type TextFormatType,
} from "lexical";
import styles from "@mylomail/renderer/Components/Editor/EditorToolbar/EditorToolbar.module.css";

type BlockType = "paragraph" | HeadingTagType | "quote";

const blockTypes: { value: BlockType; label: string }[] = [
	{ value: "paragraph", label: "Normal" },
	{ value: "h1", label: "Heading 1" },
	{ value: "h2", label: "Heading 2" },
	{ value: "h3", label: "Heading 3" },
	{ value: "quote", label: "Quote" },
];

const fontSizes = [
	{ value: "", label: "Normal size" },
	{ value: "12px", label: "Small" },
	{ value: "18px", label: "Large" },
	{ value: "24px", label: "Larger" },
	{ value: "32px", label: "Largest" },
];

/**
 * The compose formatting set: block style, size, inline formatting, colour, lists, alignment,
 * indentation, links and history. Everything it applies is carried by the message as ordinary
 * elements and inline styles, which is what mail clients render.
 */
export function EditorToolbar() {
	const [editor] = useLexicalComposerContext();
	const [linkUrl, setLinkUrl] = useState<string | null>(null);
	const [blockType, setBlockType] = useState<BlockType>("paragraph");

	useEffect(() => {
		const read = () => {
			const selection = $getSelection();
			if (!$isRangeSelection(selection)) return;
			const block = selection.anchor.getNode().getTopLevelElementOrThrow();
			setBlockType(
				$isHeadingNode(block)
					? block.getTag()
					: $isQuoteNode(block)
						? "quote"
						: "paragraph",
			);
		};
		const unregisterUpdates = editor.registerUpdateListener(({ editorState }) =>
			editorState.read(read),
		);
		const unregisterSelection = editor.registerCommand(
			SELECTION_CHANGE_COMMAND,
			() => {
				read();
				return false;
			},
			COMMAND_PRIORITY_LOW,
		);
		return () => {
			unregisterUpdates();
			unregisterSelection();
		};
	}, [editor]);

	const button = (label: string, Icon: CarbonIconType, run: () => void) => (
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

	const text = (format: TextFormatType) => () =>
		editor.dispatchCommand(FORMAT_TEXT_COMMAND, format);
	const align = (format: ElementFormatType) => () =>
		editor.dispatchCommand(FORMAT_ELEMENT_COMMAND, format);

	// A select takes keyboard focus; typing carries on in the message afterwards.
	const setBlock = (type: BlockType) => {
		editor.update(
			() => {
				const selection = $getSelection();
				if (!$isRangeSelection(selection)) return;
				$setBlocksType(selection, () =>
					type === "paragraph"
						? $createParagraphNode()
						: type === "quote"
							? $createQuoteNode()
							: $createHeadingNode(type),
				);
			},
			{ onUpdate: () => editor.focus() },
		);
	};

	const style = (property: string, value: string | null, refocus = false) =>
		editor.update(
			() => {
				const selection = $getSelection();
				if ($isRangeSelection(selection))
					$patchStyleText(selection, { [property]: value });
			},
			refocus ? { onUpdate: () => editor.focus() } : undefined,
		);

	const clearFormatting = () =>
		editor.update(() => {
			const selection = $getSelection();
			if (!$isRangeSelection(selection)) return;
			$patchStyleText(selection, {
				color: null,
				"background-color": null,
				"font-size": null,
			});
			for (const node of selection.getNodes()) {
				if ($isTextNode(node)) node.setFormat(0);
			}
			$setBlocksType(selection, () => $createParagraphNode());
		});

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
			<div className={styles.select}>
				<Select
					id="compose-block-type"
					size="sm"
					labelText="Paragraph style"
					hideLabel
					value={blockType}
					onChange={(event) => setBlock(event.target.value as BlockType)}
				>
					{blockTypes.map((type) => (
						<SelectItem key={type.value} value={type.value} text={type.label} />
					))}
				</Select>
			</div>
			<div className={styles.select}>
				<Select
					id="compose-font-size"
					size="sm"
					labelText="Text size"
					hideLabel
					defaultValue=""
					onChange={(event) =>
						style("font-size", event.target.value || null, true)
					}
				>
					{fontSizes.map((size) => (
						<SelectItem key={size.value} value={size.value} text={size.label} />
					))}
				</Select>
			</div>
			<span className={styles.separator} />
			{button("Bold", TextBold, text("bold"))}
			{button("Italic", TextItalic, text("italic"))}
			{button("Underline", TextUnderline, text("underline"))}
			{button("Strikethrough", TextStrikethrough, text("strikethrough"))}
			{button("Subscript", TextSubscript, text("subscript"))}
			{button("Superscript", TextSuperscript, text("superscript"))}
			<label className={styles.colour}>
				<TextColor aria-hidden="true" />
				<input
					type="color"
					aria-label="Text colour"
					defaultValue="#161616"
					onChange={(event) => style("color", event.target.value)}
				/>
			</label>
			<label className={styles.colour}>
				<TextHighlight aria-hidden="true" />
				<input
					type="color"
					aria-label="Highlight colour"
					defaultValue="#f1c21b"
					onChange={(event) => style("background-color", event.target.value)}
				/>
			</label>
			{button("Clear formatting", TextClearFormat, clearFormatting)}
			<span className={styles.separator} />
			{button("Bulleted list", ListBulleted, () =>
				editor.dispatchCommand(INSERT_UNORDERED_LIST_COMMAND, undefined),
			)}
			{button("Numbered list", ListNumbered, () =>
				editor.dispatchCommand(INSERT_ORDERED_LIST_COMMAND, undefined),
			)}
			{button("Decrease indent", TextIndentLess, () =>
				editor.dispatchCommand(OUTDENT_CONTENT_COMMAND, undefined),
			)}
			{button("Increase indent", TextIndentMore, () =>
				editor.dispatchCommand(INDENT_CONTENT_COMMAND, undefined),
			)}
			<span className={styles.separator} />
			{button("Align left", TextAlignLeft, align("left"))}
			{button("Align centre", TextAlignCenter, align("center"))}
			{button("Align right", TextAlignRight, align("right"))}
			{button("Justify", TextAlignJustify, align("justify"))}
			<span className={styles.separator} />
			{button("Link", Link, () => setLinkUrl(linkUrl === null ? "" : null))}
			{button("Remove link", Unlink, () =>
				editor.dispatchCommand(TOGGLE_LINK_COMMAND, null),
			)}
			<span className={styles.separator} />
			{button("Undo", Undo, () =>
				editor.dispatchCommand(UNDO_COMMAND, undefined),
			)}
			{button("Redo", Redo, () =>
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
