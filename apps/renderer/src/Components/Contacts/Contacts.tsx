import { useQuery, useQueryClient } from "@tanstack/react-query";
import {
	ActionableNotification,
	Button,
	InlineNotification,
	TextInput,
} from "@carbon/react";
import { useState } from "react";
import type { HubConnection } from "@microsoft/signalr";
import type {
	ContactDto,
	SaveContactRequest,
} from "@mylomail/shared-types/SignalR/MyloMail.Api.Contracts";
import { ProviderType } from "@mylomail/shared-types/SignalR/MyloMail.Api.Domain";
import { queryKeys } from "@mylomail/renderer/Shell/Backend/HubConnection";

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
	hub: HubConnection;
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
			hub.invoke<ContactDto[]>(
				query ? "SearchContacts" : "GetContacts",
				accountId,
				...(query ? [query] : []),
			),
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
		void run(() => hub.invoke("SaveContact", request));
	};

	return (
		<section aria-label="Contacts">
			<h2>Contacts</h2>
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
				id="contacts-search"
				labelText="Search contacts"
				value={query}
				onChange={(event) => setQuery(event.target.value)}
			/>
			<ul>
				{contacts.data?.map((contact) => (
					<li key={contact.id}>
						<strong>{contact.displayName}</strong>{" "}
						{contact.addresses.map((address) => address.email).join(", ")}{" "}
						{contact.syncConflict ? <span>Conflict</span> : null}{" "}
						{contact.syncPending ? <span>Syncing</span> : null}{" "}
						{contact.ambiguousOutcome ? (
							<span>Needs reconciliation</span>
						) : null}
						{contact.syncRejected ? (
							<span>Provider rejected — edit to retry</span>
						) : null}{" "}
						<Button
							size="sm"
							kind="ghost"
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
										hub.invoke("DeleteContact", {
											contactId: contact.id,
											expectedRevision: contact.providerRevision,
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
											hub.invoke("AbandonAmbiguousContactCreate", contact.id),
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
											hub.invoke("ResolveContactConflict", contact.id, true),
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
											hub.invoke("ResolveContactConflict", contact.id, false),
										)
									}
								>
									Keep theirs
								</Button>
							</>
						) : null}
					</li>
				))}
			</ul>
			{editing ? (
				<div>
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
			) : (
				<Button
					size="sm"
					kind="tertiary"
					disabled={busy}
					onClick={() => setEditing(blankContact)}
				>
					Add contact
				</Button>
			)}
		</section>
	);
}
