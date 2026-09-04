import { useEffect, useState } from "react";
import { Button, InlineNotification, TextArea, TextInput } from "@carbon/react";
import type { HubConnection } from "@microsoft/signalr";
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

/**
 * Send-as identity management (§1, §15): a real alias scenario (e.g. an address on the same
 * account distinct from the default) needs its own display name, address and signature —
 * added, edited, deleted, or promoted to default here, independently of the rest of the
 * account settings form above it, so a mistake here never risks the unsaved edits there.
 */
export function SendIdentityManager({
	hub,
	accountId,
}: {
	hub: HubConnection;
	accountId: string;
}) {
	const [identities, setIdentities] = useState<SendIdentityDto[] | null>(null);
	const [editing, setEditing] = useState<EditingIdentity | null>(null);
	const [error, setError] = useState<string | null>(null);
	const [busy, setBusy] = useState(false);

	const reload = () =>
		hub
			.invoke<SendIdentityDto[]>("GetSendIdentities", accountId)
			.then(setIdentities)
			.catch((thrown: unknown) => {
				// Unlike `run`'s failures (a save/delete the user just triggered), this one
				// fires from the mount effect below with no action of the user's own to blame
				// it on — surfacing it the same way keeps a failed initial load from silently
				// rendering as "this account simply has no identities."
				setError(thrown instanceof Error ? thrown.message : String(thrown));
			});

	useEffect(() => {
		void reload();
		// eslint-disable-next-line react-hooks/exhaustive-deps -- reload closes over stable hub/accountId props
	}, [hub, accountId]);

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

	const saveEditing = () => {
		if (!editing) return;
		const { id, displayName, emailAddress, signatureHtml } = editing;
		void run(() =>
			id
				? hub.invoke(
						"UpdateSendIdentity",
						id,
						displayName,
						emailAddress,
						signatureHtml || null,
					)
				: hub.invoke(
						"AddSendIdentity",
						accountId,
						displayName,
						emailAddress,
						signatureHtml || null,
					),
		);
	};

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
										void run(() =>
											hub.invoke("SetDefaultSendIdentity", identity.id),
										)
									}
								>
									Make default
								</Button>
							)}
							<Button
								size="sm"
								kind="danger--ghost"
								disabled={busy}
								onClick={() =>
									void run(() => hub.invoke("DeleteSendIdentity", identity.id))
								}
							>
								Delete
							</Button>
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
							disabled={busy || !editing.displayName || !editing.emailAddress}
							onClick={saveEditing}
						>
							{editing.id ? "Save" : "Add"}
						</Button>
						<Button
							size="sm"
							kind="ghost"
							disabled={busy}
							onClick={() => setEditing(null)}
						>
							Cancel
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
