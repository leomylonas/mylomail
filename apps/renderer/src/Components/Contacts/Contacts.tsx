import { useQuery, useQueryClient } from "@tanstack/react-query";
import type { MailHubConnection } from "@mylomail/renderer/Shell/Backend/HubConnection";
import {
	ActionableNotification,
	Button,
	InlineNotification,
	TextInput,
} from "@carbon/react";
import { useState } from "react";
import { Add, Edit } from "@carbon/icons-react";
import type { SaveContactRequest } from "@mylomail/shared-types/SignalR/MyloMail.Api.Contracts";
import { ProviderType } from "@mylomail/shared-types/SignalR/MyloMail.Api.Domain";
import { queryKeys } from "@mylomail/renderer/Shell/Backend/HubConnection";
import styles from "@mylomail/renderer/Components/Contacts/Contacts.module.css";

interface ContactEditor {
	id?: string;
	displayName: string;
	emails: string;
	expectedRevision?: string;
}

const blankContact: ContactEditor = { displayName: "", emails: "" };

/** Account-local contact management; provider conflicts remain explicit rather than merged. */
export function Contacts({
	hub,
	accountId,
	providerType,
}: {
	hub: MailHubConnection;
	accountId: string;
	providerType: ProviderType;
}) {
	const queryClient = useQueryClient();
	const [query, setQuery] = useState("");
	const [editing, setEditing] = useState<ContactEditor | null>(null);
	const [busy, setBusy] = useState(false);
	const [error, setError] = useState<string | null>(null);
	const contacts = useQuery({
		queryKey: queryKeys.contacts(accountId, query),
		queryFn: () =>
			query ? hub.searchContacts(accountId, query) : hub.getContacts(accountId),
	});
	const providerSupportsDeletion = providerType !== ProviderType.Gmail;

	const invalidate = () =>
		queryClient.invalidateQueries({ queryKey: ["contacts", accountId] });
	const run = async (action: () => Promise<unknown>) => {
		setBusy(true);
		setError(null);
		try {
			await action();
			await invalidate();
			setEditing(null);
		} catch (thrown) {
			setError(thrown instanceof Error ? thrown.message : String(thrown));
		} finally {
			setBusy(false);
		}
	};
	const save = () => {
		if (!editing) return;
		const request: SaveContactRequest = {
			contactId: editing.id,
			accountId,
			displayName: editing.displayName.trim(),
			emails: editing.emails
				.split(/[;,\n]/)
				.map((email) => email.trim())
				.filter(Boolean),
			expectedRevision: editing.expectedRevision,
		};
		void run(() => hub.saveContact(request));
	};

	return (
		<section className={styles.contacts} aria-label="Contacts">
			<header className={styles.pageHeader}>
				<div>
					<span className={styles.eyebrow}>Address book</span>
					<h2>People</h2>
					<p>Contacts for the selected account</p>
				</div>
				{editing ? null : (
					<Button
						size="sm"
						renderIcon={Add}
						disabled={busy}
						onClick={() => setEditing(blankContact)}
					>
						Add contact
					</Button>
				)}
			</header>
			<div className={styles.content}>
				{error ? (
					<InlineNotification
						kind="error"
						title="Couldn't update contacts"
						subtitle={error}
						onCloseButtonClick={() => setError(null)}
						lowContrast
					/>
				) : null}
				{contacts.isError ? (
					<ActionableNotification
						kind="error"
						title="Couldn't load contacts"
						subtitle={
							contacts.error instanceof Error
								? contacts.error.message
								: String(contacts.error)
						}
						actionButtonLabel="Retry"
						onActionButtonClick={() => void contacts.refetch()}
						lowContrast
					/>
				) : null}
				{!providerSupportsDeletion ? (
					<InlineNotification
						kind="info"
						title="Google contact deletion unavailable"
						subtitle="Google does not support the revision-checked deletion MyloMail requires."
						lowContrast
						hideCloseButton
					/>
				) : null}
				<TextInput
					className={styles.search}
					id="contacts-search"
					labelText="Search contacts"
					value={query}
					onChange={(event) => setQuery(event.target.value)}
				/>
				<ul className={styles.contactList}>
					{contacts.data?.map((contact) => (
						<li key={contact.id} className={styles.contact}>
							<div className={styles.contactIdentity}>
								<span className={styles.avatar} aria-hidden="true">
									{contact.displayName.trim().charAt(0).toLocaleUpperCase() ||
										"?"}
								</span>
								<div className={styles.contactText}>
									<strong>{contact.displayName}</strong>
									<span>
										{contact.addresses
											.map((address) => address.email)
											.join(", ")}
									</span>
								</div>
							</div>
							<div className={styles.statuses}>
								{contact.syncConflict ? <span>Conflict</span> : null}
								{contact.syncPending ? <span>Syncing</span> : null}
								{contact.ambiguousOutcome ? (
									<span>Needs reconciliation</span>
								) : null}
								{contact.syncRejected ? (
									<span>Provider rejected — edit to retry</span>
								) : null}
							</div>
							<div className={styles.contactActions}>
								<Button
									size="sm"
									kind="ghost"
									renderIcon={Edit}
									disabled={busy}
									onClick={() =>
										setEditing({
											id: contact.id,
											displayName: contact.displayName,
											emails: contact.addresses
												.map((address) => address.email)
												.join(", "),
											expectedRevision: contact.providerRevision,
										})
									}
								>
									Edit
								</Button>
								<Button
									size="sm"
									kind="danger--ghost"
									disabled={busy || !contact.canDelete}
									onClick={() => {
										if (window.confirm(`Delete ${contact.displayName}?`)) {
											void run(() =>
												hub.deleteContact({
													contactId: contact.id,
													expectedRevision:
														contact.providerRevision ?? undefined,
												}),
											);
										}
									}}
								>
									Delete
								</Button>
								{contact.ambiguousCreate ? (
									<Button
										size="sm"
										kind="danger--tertiary"
										disabled={busy}
										onClick={() => {
											if (
												window.confirm(
													"Discard this local contact intent? Any remote contact created before the connection was lost will remain on the provider.",
												)
											) {
												void run(() =>
													hub.abandonAmbiguousContactCreate(contact.id),
												);
											}
										}}
									>
										Discard local intent
									</Button>
								) : null}
								{contact.syncConflict ? (
									<>
										<Button
											size="sm"
											kind="tertiary"
											disabled={busy}
											onClick={() =>
												void run(() =>
													hub.resolveContactConflict(contact.id, true),
												)
											}
										>
											Keep mine
										</Button>
										<Button
											size="sm"
											kind="tertiary"
											disabled={busy}
											onClick={() =>
												void run(() =>
													hub.resolveContactConflict(contact.id, false),
												)
											}
										>
											Keep theirs
										</Button>
									</>
								) : null}
							</div>
						</li>
					))}
				</ul>
				{editing ? (
					<div className={styles.editor}>
						<h3>{editing.id ? "Edit contact" : "New contact"}</h3>
						<TextInput
							id="contact-display-name"
							labelText="Display name"
							value={editing.displayName}
							onChange={(event) =>
								setEditing({ ...editing, displayName: event.target.value })
							}
						/>
						<TextInput
							id="contact-emails"
							labelText="Email addresses"
							helperText="Separate multiple addresses with commas"
							value={editing.emails}
							onChange={(event) =>
								setEditing({ ...editing, emails: event.target.value })
							}
						/>
						<div className={styles.editorActions}>
							<Button
								size="sm"
								disabled={
									busy || !editing.displayName.trim() || !editing.emails.trim()
								}
								onClick={save}
							>
								Save
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
				) : null}
			</div>
		</section>
	);
}
