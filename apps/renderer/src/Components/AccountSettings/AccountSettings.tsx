import { useState } from "react";
import {
	Button,
	InlineNotification,
	NumberInput,
	TextInput,
	Toggle,
} from "@carbon/react";
import type { HubConnection } from "@microsoft/signalr";
import { CertificateTrustMode } from "@mylomail/shared-types/SignalR/MyloMail.Api.Domain";
import { ExportAccount } from "@mylomail/renderer/Components/ExportAccount/ExportAccount";
import { ensureAccentContrast } from "@mylomail/renderer/Components/AccountSettings/AccentContrast";
import { SendIdentityManager } from "@mylomail/renderer/Components/AccountSettings/SendIdentityManager/SendIdentityManager";
import { useWindowNotifications } from "@mylomail/renderer/Shell/Registries/Notifications/UseNotifications";
import { notify } from "@mylomail/renderer/Shell/Registries/Notifications/NotificationStore";
import styles from "@mylomail/renderer/Components/AccountSettings/AccountSettings.module.css";

export interface AccountSettingsValues {
	id: string;
	displayName: string;
	color: string;
	pollIntervalSeconds: number;
	pollingEnabled: boolean;
	undoSendDelaySeconds: number;
	notificationsEnabled: boolean;
	certificateTrustMode: CertificateTrustMode;
	/** Bytes. Null leaves it unset — the provider's own limit (or "unknown") applies (§15). */
	attachmentSizeLimitOverride: number | null;
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
	const { store: notifications } = useWindowNotifications();

	const save = async () => {
		try {
			// The server clamps the poll interval and undo window and returns what it
			// applied, so the form shows the value in force rather than the one asked for.
			const applied = await hub.invoke<AccountSettingsValues>(
				"UpdateAccount",
				values,
			);
			setValues(applied);
			setSaved(true);
		} catch (error) {
			notify(notifications, {
				kind: "error",
				title: "These settings could not be saved",
				detail: error instanceof Error ? error.message : String(error),
			});
		}
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
			<label className={styles.colorField} htmlFor="settings-color">
				Colour
				<input
					id="settings-color"
					type="color"
					// A never-saved account has no colour yet; a neutral grey is a better
					// starting point in the picker than the browser's own black default.
					value={values.color || "#8d8d8d"}
					onChange={(event) =>
						setValues({
							...values,
							// A contrast floor, not whatever the OS picker happened to
							// return (§13 Accessibility): near-white or near-black picks
							// would make the sidebar swatch effectively invisible against
							// one of the two themes.
							color: ensureAccentContrast(event.target.value),
						})
					}
				/>
			</label>
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
			<Toggle
				id="settings-trust-all-certificates"
				labelText="Trust any server certificate for this account"
				toggled={values.certificateTrustMode === CertificateTrustMode.TrustAll}
				onToggle={(checked) =>
					setValues({
						...values,
						certificateTrustMode: checked
							? CertificateTrustMode.TrustAll
							: CertificateTrustMode.Default,
					})
				}
			/>
			{values.certificateTrustMode === CertificateTrustMode.TrustAll ? (
				<InlineNotification
					kind="warning"
					title="This is a genuine security downgrade"
					subtitle="MyloMail will accept any certificate this server presents, including one an
						attacker controls. Only leave this on for a self-hosted server whose certificate
						you can't otherwise get trusted, and prefer pinning a specific certificate instead
						once you can."
					lowContrast
					hideCloseButton
				/>
			) : null}
			<NumberInput
				id="settings-attachment-limit"
				label="Maximum attachment size override (MB)"
				helperText="Leave at 0 to use the provider's own limit."
				min={0}
				value={
					values.attachmentSizeLimitOverride
						? Math.round(values.attachmentSizeLimitOverride / (1024 * 1024))
						: 0
				}
				onChange={(_, { value }) =>
					setValues({
						...values,
						attachmentSizeLimitOverride:
							Number(value) > 0
								? Math.round(Number(value) * 1024 * 1024)
								: null,
					})
				}
			/>
			<SendIdentityManager hub={hub} accountId={values.id} />
			<ExportAccount hub={hub} accountId={values.id} />
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
