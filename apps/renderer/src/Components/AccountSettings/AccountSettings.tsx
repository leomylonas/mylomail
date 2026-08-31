import { useState } from "react";
import { Button, NumberInput, TextInput, Toggle } from "@carbon/react";
import type { HubConnection } from "@microsoft/signalr";
import styles from "@mylomail/renderer/Components/AccountSettings/AccountSettings.module.css";

export interface AccountSettingsValues {
	id: string;
	displayName: string;
	color: string;
	pollIntervalSeconds: number;
	pollingEnabled: boolean;
	undoSendDelaySeconds: number;
	notificationsEnabled: boolean;
}

/**
 * The account settings a user owns.
 *
 * Only these: authentication state and sync progress are the system's to write, and putting
 * them on a settings form would make the UI a second source of truth for facts it does not
 * observe (§1).
 */
export function AccountSettings({
	hub,
	initial,
	onClose,
}: {
	hub: HubConnection;
	initial: AccountSettingsValues;
	onClose: () => void;
}) {
	const [values, setValues] = useState(initial);
	const [saved, setSaved] = useState(false);

	const save = async () => {
		// The server clamps the poll interval and undo window and returns what it applied, so
		// the form shows the value in force rather than the one that was asked for.
		const applied = await hub.invoke<AccountSettingsValues>(
			"UpdateAccount",
			values,
		);
		setValues(applied);
		setSaved(true);
	};

	return (
		<div className={styles.settings}>
			<TextInput
				id="settings-name"
				labelText="Account name"
				value={values.displayName}
				onChange={(event) =>
					setValues({ ...values, displayName: event.target.value })
				}
			/>
			<NumberInput
				id="settings-poll"
				label="Check for mail every (seconds)"
				min={15}
				value={values.pollIntervalSeconds}
				onChange={(_, { value }) =>
					setValues({ ...values, pollIntervalSeconds: Number(value) })
				}
			/>
			<NumberInput
				id="settings-undo"
				label="Undo send window (seconds)"
				min={0}
				value={values.undoSendDelaySeconds}
				helperText="Zero sends immediately. Nothing can be recalled once sent."
				onChange={(_, { value }) =>
					setValues({ ...values, undoSendDelaySeconds: Number(value) })
				}
			/>
			<Toggle
				id="settings-polling"
				labelText="Check for new mail"
				toggled={values.pollingEnabled}
				onToggle={(checked) =>
					setValues({ ...values, pollingEnabled: checked })
				}
			/>
			<Toggle
				id="settings-notifications"
				labelText="Desktop notifications"
				toggled={values.notificationsEnabled}
				onToggle={(checked) =>
					setValues({ ...values, notificationsEnabled: checked })
				}
			/>
			<div>
				<Button size="sm" onClick={() => void save()}>
					Save
				</Button>
				<Button size="sm" kind="ghost" onClick={onClose}>
					Close
				</Button>
				{saved ? <span> Saved.</span> : null}
			</div>
		</div>
	);
}
