import { useState } from "react";
import {
	Button,
	InlineNotification,
	Modal,
	NumberInput,
	TextInput,
	Toggle,
} from "@carbon/react";
import type { HubConnection } from "@microsoft/signalr";
import {
	CertificateTrustMode,
	ProviderType,
} from "@mylomail/shared-types/SignalR/MyloMail.Api.Domain";
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
	/** Not itself sent to `UpdateAccount` beyond deciding whether the toggle below renders. */
	providerType: ProviderType;
	/** IMAP only; null for every other provider (§15). */
	appendToSentOnSend: boolean | null;
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
	onRemoved,
}: {
	hub: HubConnection;
	initial: AccountSettingsValues;
	onClose: () => void;
	/**
	 * Called after the account is actually removed server-side, separately from `onClose`:
	 * the shell needs to know to stop treating this account as selected (and pick another, or
	 * fall back to "add an account"), not just close this settings pane.
	 */
	onRemoved: () => void;
}) {
	const [values, setValues] = useState(initial);
	const [saved, setSaved] = useState(false);
	const [confirmingRemove, setConfirmingRemove] = useState(false);
	const [removing, setRemoving] = useState(false);
	// Set only when a first, unforced removal attempt reports a still-running export — a
	// second, separate confirmation asks whether to stop it (§13 Export) rather than silently
	// either blocking the removal or abandoning the export.
	const [exportConflict, setExportConflict] = useState(false);
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

	const remove = async (force = false) => {
		setRemoving(true);
		try {
			const response = await fetch(
				`/accounts/${values.id}${force ? "?force=true" : ""}`,
				{ method: "DELETE" },
			);
			if (response.status === 409 && !force) {
				// A bulk export is still running for this account — ask separately whether to
				// stop it rather than either silently blocking or silently abandoning it.
				setConfirmingRemove(false);
				setExportConflict(true);
				return;
			}
			if (!response.ok)
				throw new Error(`accounts responded ${response.status}`);
			setConfirmingRemove(false);
			setExportConflict(false);
			onRemoved();
		} catch (error) {
			notify(notifications, {
				kind: "error",
				title: "This account could not be removed",
				detail: error instanceof Error ? error.message : String(error),
			});
		} finally {
			setRemoving(false);
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
			{values.providerType === ProviderType.Imap ? (
				<Toggle
					id="settings-append-to-sent"
					labelText="Save a copy to Sent when sending"
					toggled={values.appendToSentOnSend ?? true}
					onToggle={(checked) =>
						setValues({ ...values, appendToSentOnSend: checked })
					}
				/>
			) : null}
			<SendIdentityManager hub={hub} accountId={values.id} />
			<ExportAccount hub={hub} accountId={values.id} />
			<section className={styles.dangerZone}>
				<h4>Remove account</h4>
				<p>
					Removes this account from MyloMail: its local messages, mailboxes and
					settings are deleted, and its stored credentials are removed. This
					does not delete anything from the mail server itself.
				</p>
				<Button
					size="sm"
					kind="danger--tertiary"
					onClick={() => setConfirmingRemove(true)}
				>
					Remove account…
				</Button>
			</section>
			{confirmingRemove ? (
				<Modal
					open
					danger
					modalHeading={`Remove "${values.displayName || "this account"}"?`}
					primaryButtonText="Remove"
					secondaryButtonText="Cancel"
					primaryButtonDisabled={removing}
					onRequestSubmit={() => void remove()}
					onRequestClose={() => setConfirmingRemove(false)}
					onSecondarySubmit={() => setConfirmingRemove(false)}
				>
					<p>
						This removes the account and its local data from MyloMail and cannot
						be undone here. Anything already in flight when you remove it — a
						message already being sent, or a move or delete already under way —
						may still complete on the server even after the account is gone from
						MyloMail; removing the account does not reliably cancel it.
					</p>
				</Modal>
			) : null}
			{exportConflict ? (
				<Modal
					open
					danger
					modalHeading="This account has an export still running"
					primaryButtonText="Remove anyway"
					secondaryButtonText="Let the export finish"
					primaryButtonDisabled={removing}
					onRequestSubmit={() => void remove(true)}
					onRequestClose={() => setExportConflict(false)}
					onSecondarySubmit={() => setExportConflict(false)}
				>
					<p>
						Removing this account now will stop the export partway through
						&mdash; any messages not yet written won&rsquo;t be. Choose
						&ldquo;Let the export finish&rdquo; to keep the account for now, or
						&ldquo;Remove anyway&rdquo; to stop the export and remove the
						account immediately.
					</p>
				</Modal>
			) : null}
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
