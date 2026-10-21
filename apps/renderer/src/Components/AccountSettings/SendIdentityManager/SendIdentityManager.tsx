import { useCallback, useEffect, useState } from "react";
import type { MailHubConnection } from "@mylomail/renderer/Shell/Backend/HubConnection";
import { Button, InlineNotification, TextArea, TextInput } from "@carbon/react";
import type { SendIdentityDto } from "@mylomail/shared-types/SignalR/MyloMail.Api.Contracts";
import styles from "@mylomail/renderer/Components/AccountSettings/SendIdentityManager/SendIdentityManager.module.css";

interface EditingIdentity {
	id?: string;
	displayName: string;
	emailAddress: string;
	signatureHtml: string;
}

const blankIdentity: EditingIdentity = {
	displayName: "",
	emailAddress: "",
	signatureHtml: "",
};

/** Pulled out of the click handler so the confirmation copy is directly testable without a
 * component-render harness (this repo has none — see passes 91-93/133/147). */
export function confirmDeleteSendIdentity(
	identity: Pick<SendIdentityDto, "displayName" | "emailAddress">,
	confirm: (message: string) => boolean,
): boolean {
	return confirm(
		`Delete the send-as identity "${identity.displayName} <${identity.emailAddress}>"? This cannot be undone.`,
	);
}

/**
 * Send-as identity management (§1, §15): a real alias scenario (e.g. an address on the same
 * account distinct from the default) needs its own display name, address and signature —
 * added, edited, deleted, or promoted to default here. Deleting and promoting act at once (the
 * first asks to confirm); an add or an edit is written by the settings page's own Save, so the
 * page has one Save whatever was changed.
 */
export function SendIdentityManager({
	hub,
	accountId,
	registerCommit,
	onPendingChange,
}: {
	hub: MailHubConnection;
	accountId: string;
	/**
	 * Hands the owner the function that writes an open add/edit form. The account settings' one
	 * Save button calls it, so changing an identity is saved the same way as everything else on
	 * the page rather than by a second button with its own meaning.
	 */
	registerCommit: (commit: (() => Promise<boolean>) | null) => void;
	/** Whether an add/edit form is open and so not yet saved. */
	onPendingChange: (pending: boolean) => void;
}) {
	const [identities, setIdentities] = useState<SendIdentityDto[] | null>(null);
	const [editing, setEditing] = useState<EditingIdentity | null>(null);
	const [error, setError] = useState<string | null>(null);
	const [busy, setBusy] = useState(false);

	const reload = useCallback(
		() =>
			hub
				.getSendIdentities(accountId)
				.then(setIdentities)
				.catch((thrown: unknown) => {
					// Unlike `run`'s failures (a save/delete the user just triggered), this one
					// fires from the mount effect below with no action of the user's own to blame
					// it on — surfacing it the same way keeps a failed initial load from silently
					// rendering as "this account simply has no identities."
					setError(thrown instanceof Error ? thrown.message : String(thrown));
				}),
		[accountId, hub],
	);

	useEffect(() => {
		void reload();
		const subscription = hub.subscribe(
			"sendIdentitiesChanged",
			(changedAccountId: string) => {
				if (changedAccountId === accountId) void reload();
			},
		);
		return () => subscription.dispose();
	}, [accountId, hub, reload]);

	const run = async (action: () => Promise<unknown>) => {
		setBusy(true);
		setError(null);
		try {
			await action();
			await reload();
			setEditing(null);
		} catch (thrown) {
			setError(thrown instanceof Error ? thrown.message : String(thrown));
		} finally {
			setBusy(false);
		}
	};

	/** Writes the open form. False when it could not be written, with the reason shown here. */
	const commit = useCallback(async (): Promise<boolean> => {
		if (!editing) return true;
		const { id, displayName, emailAddress, signatureHtml } = editing;
		if (!displayName.trim() || !emailAddress.trim()) {
			setError("An identity needs a display name and an email address.");
			return false;
		}
		setBusy(true);
		setError(null);
		try {
			if (id)
				await hub.updateSendIdentity(
					id,
					displayName,
					emailAddress,
					signatureHtml,
				);
			else
				await hub.addSendIdentity(
					accountId,
					displayName,
					emailAddress,
					signatureHtml,
				);
			await reload();
			setEditing(null);
			return true;
		} catch (thrown) {
			setError(thrown instanceof Error ? thrown.message : String(thrown));
			return false;
		} finally {
			setBusy(false);
		}
	}, [accountId, editing, hub, reload]);

	useEffect(() => {
		registerCommit(commit);
		return () => registerCommit(null);
	}, [commit, registerCommit]);
	useEffect(() => {
		onPendingChange(editing !== null);
	}, [editing, onPendingChange]);

	return (
		<div className={styles.manager}>
			<h3 className={styles.heading}>Send-as identities</h3>
			{error ? (
				<InlineNotification
					kind="error"
					title="Couldn't complete that"
					subtitle={error}
					onCloseButtonClick={() => setError(null)}
					lowContrast
				/>
			) : null}
			<ul className={styles.list}>
				{(identities ?? []).map((identity) => (
					<li key={identity.id} className={styles.row}>
						<span className={styles.summary}>
							{identity.isDefault ? "●" : "○"} {identity.displayName} &lt;
							{identity.emailAddress}&gt;
							{identity.isDefault ? " (default)" : ""}
						</span>
						<span className={styles.actions}>
							<Button
								size="sm"
								kind="ghost"
								disabled={busy}
								onClick={() =>
									setEditing({
										id: identity.id,
										displayName: identity.displayName,
										emailAddress: identity.emailAddress,
										signatureHtml: identity.signatureHtml ?? "",
									})
								}
							>
								Edit
							</Button>
							{identity.isDefault ? null : (
								<Button
									size="sm"
									kind="ghost"
									disabled={busy}
									onClick={() =>
										void run(() => hub.setDefaultSendIdentity(identity.id))
									}
								>
									Make default
								</Button>
							)}
							{identity.isDefault ? null : (
								<Button
									size="sm"
									kind="danger--ghost"
									disabled={busy}
									onClick={() => {
										if (!confirmDeleteSendIdentity(identity, window.confirm)) {
											return;
										}
										void run(() => hub.deleteSendIdentity(identity.id));
									}}
								>
									Delete
								</Button>
							)}
						</span>
					</li>
				))}
			</ul>
			{editing ? (
				<div className={styles.form}>
					<TextInput
						id="identity-display-name"
						labelText="Display name"
						value={editing.displayName}
						onChange={(event) =>
							setEditing({ ...editing, displayName: event.target.value })
						}
					/>
					<TextInput
						id="identity-email"
						labelText="Email address"
						value={editing.emailAddress}
						onChange={(event) =>
							setEditing({ ...editing, emailAddress: event.target.value })
						}
					/>
					<TextArea
						id="identity-signature"
						labelText="Signature (HTML)"
						value={editing.signatureHtml}
						onChange={(event) =>
							setEditing({ ...editing, signatureHtml: event.target.value })
						}
					/>
					<div className={styles.formActions}>
						<Button
							size="sm"
							kind="ghost"
							disabled={busy}
							onClick={() => setEditing(null)}
						>
							Discard changes
						</Button>
					</div>
				</div>
			) : (
				<Button
					size="sm"
					kind="tertiary"
					onClick={() => setEditing(blankIdentity)}
				>
					Add identity
				</Button>
			)}
		</div>
	);
}
