import { useState } from "react";
import { Modal, TextInput } from "@carbon/react";

/**
 * Asks for a folder name.
 *
 * A component rather than `window.prompt`, which is not merely discouraged in Electron but
 * absent: it throws `prompt() is not supported`, so the folder actions that used it did
 * nothing at all in the packaged app while working in a browser.
 */
export function FolderNameModal({
	heading,
	label,
	initialName,
	primaryLabel,
	onSubmit,
	onClose,
}: {
	heading: string;
	label: string;
	initialName?: string;
	primaryLabel: string;
	onSubmit: (name: string) => void;
	onClose: () => void;
}) {
	// Seeded once. Reopening for a different folder must not carry the first folder's name
	// over, which the caller ensures by keying this component on the folder rather than by
	// synchronising state in an effect.
	const [name, setName] = useState(initialName ?? "");

	const trimmed = name.trim();

	return (
		<Modal
			open
			modalHeading={heading}
			primaryButtonText={primaryLabel}
			secondaryButtonText="Cancel"
			primaryButtonDisabled={trimmed.length === 0}
			onRequestSubmit={() => {
				if (trimmed.length > 0) onSubmit(trimmed);
			}}
			onRequestClose={onClose}
			onSecondarySubmit={onClose}
		>
			<TextInput
				id="folder-name"
				data-modal-primary-focus
				labelText={label}
				value={name}
				onChange={(event) => setName(event.target.value)}
			/>
		</Modal>
	);
}
