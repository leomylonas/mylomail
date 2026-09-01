import { useState } from "react";
import { useMutation } from "@tanstack/react-query";
import {
	Button,
	InlineNotification,
	NumberInput,
	PasswordInput,
	RadioButton,
	RadioButtonGroup,
	TextInput,
	Toggle,
} from "@carbon/react";
import { ProviderType } from "@mylomail/shared-types/SignalR/MyloMail.Api.Domain";
import type { AddAccountRequest } from "@mylomail/shared-types/SignalR/MyloMail.Api.Contracts";
import styles from "@mylomail/renderer/Components/AddAccount/AddAccount.module.css";

interface FormState {
	displayName: string;
	providerType: ProviderType;
	emailAddress: string;
	secret: string;
	host: string;
	port: number;
	useSsl: boolean;
	userName: string;
	smtpHost: string;
	smtpPort: number;
	reuseImapCredentialForSmtp: boolean;
	smtpUserName: string;
	smtpSecret: string;
}

const initial: FormState = {
	displayName: "",
	providerType: ProviderType.Imap,
	emailAddress: "",
	secret: "",
	host: "",
	port: 993,
	useSsl: true,
	userName: "",
	smtpHost: "",
	smtpPort: 465,
	reuseImapCredentialForSmtp: true,
	smtpUserName: "",
	smtpSecret: "",
};

/**
 * Connects the account backing the `POST /accounts` endpoint, which until now had never been
 * driven by anything but tests and manual `fetch` calls (Epic 1).
 */
export function AddAccount({ onAdded }: { onAdded: () => void }) {
	const [form, setForm] = useState(initial);
	const set = <K extends keyof FormState>(key: K, value: FormState[K]) =>
		setForm({ ...form, [key]: value });

	const add = useMutation({
		mutationFn: async () => {
			const request: AddAccountRequest = {
				displayName: form.displayName,
				providerType: form.providerType,
				emailAddress: form.emailAddress,
				secret: form.secret,
				imap: {
					host: form.host,
					port: form.port,
					useSsl: form.useSsl,
					userName: form.userName || form.emailAddress,
					smtpHost: form.smtpHost,
					smtpPort: form.smtpPort,
					reuseImapCredentialForSmtp: form.reuseImapCredentialForSmtp,
					smtpUserName: form.reuseImapCredentialForSmtp
						? undefined
						: form.smtpUserName,
					smtpSecret: form.reuseImapCredentialForSmtp
						? undefined
						: form.smtpSecret,
				},
			};

			// Same-origin, so the launch cookie authenticates this without a token — the same
			// call the account list itself already makes.
			const response = await fetch("/accounts", {
				method: "POST",
				headers: { "Content-Type": "application/json" },
				body: JSON.stringify(request),
			});
			if (!response.ok) {
				const problem = (await response.json().catch(() => null)) as {
					detail?: string;
					title?: string;
				} | null;
				throw new Error(
					problem?.detail ??
						problem?.title ??
						`Adding the account failed (${response.status}).`,
				);
			}
		},
		onSuccess: () => {
			setForm(initial);
			onAdded();
		},
	});

	const imapReady = Boolean(
		form.displayName &&
		form.emailAddress &&
		form.host &&
		form.secret &&
		form.smtpHost,
	);

	return (
		<div className={styles.form}>
			<h2 className={styles.heading}>Add account</h2>

			<RadioButtonGroup
				legendText="Provider"
				name="provider-type"
				valueSelected={String(form.providerType)}
				onChange={(value) => set("providerType", Number(value) as ProviderType)}
			>
				<RadioButton
					id="provider-imap"
					labelText="IMAP"
					value={String(ProviderType.Imap)}
				/>
				<RadioButton
					id="provider-gmail"
					labelText="Google (coming soon)"
					value={String(ProviderType.Gmail)}
					disabled
				/>
				<RadioButton
					id="provider-microsoft365"
					labelText="Microsoft 365 (coming soon)"
					value={String(ProviderType.Microsoft365)}
					disabled
				/>
			</RadioButtonGroup>

			{form.providerType === ProviderType.Imap ? (
				<>
					<TextInput
						id="add-account-name"
						labelText="Account name"
						value={form.displayName}
						onChange={(event) => set("displayName", event.target.value)}
					/>
					<TextInput
						id="add-account-email"
						labelText="Email address"
						type="email"
						value={form.emailAddress}
						onChange={(event) => set("emailAddress", event.target.value)}
					/>

					<div className={styles.row}>
						<TextInput
							id="add-account-host"
							labelText="IMAP host"
							value={form.host}
							onChange={(event) => set("host", event.target.value)}
						/>
						<NumberInput
							id="add-account-port"
							label="Port"
							value={form.port}
							onChange={(_, { value }) => set("port", Number(value))}
						/>
					</div>
					<Toggle
						id="add-account-ssl"
						labelText="Use TLS"
						toggled={form.useSsl}
						onToggle={(checked) => set("useSsl", checked)}
					/>
					<TextInput
						id="add-account-username"
						labelText="Login name"
						helperText="Leave blank to use the email address."
						value={form.userName}
						onChange={(event) => set("userName", event.target.value)}
					/>
					<PasswordInput
						id="add-account-secret"
						labelText="Password"
						value={form.secret}
						onChange={(event) => set("secret", event.target.value)}
					/>

					<div className={styles.row}>
						<TextInput
							id="add-account-smtp-host"
							labelText="SMTP host"
							value={form.smtpHost}
							onChange={(event) => set("smtpHost", event.target.value)}
						/>
						<NumberInput
							id="add-account-smtp-port"
							label="SMTP port"
							value={form.smtpPort}
							onChange={(_, { value }) => set("smtpPort", Number(value))}
						/>
					</div>
					<Toggle
						id="add-account-smtp-reuse"
						labelText="Use the IMAP password for sending"
						toggled={form.reuseImapCredentialForSmtp}
						onToggle={(checked) => set("reuseImapCredentialForSmtp", checked)}
					/>
					{form.reuseImapCredentialForSmtp ? null : (
						<>
							<TextInput
								id="add-account-smtp-username"
								labelText="SMTP login name"
								value={form.smtpUserName}
								onChange={(event) => set("smtpUserName", event.target.value)}
							/>
							<PasswordInput
								id="add-account-smtp-secret"
								labelText="SMTP password"
								value={form.smtpSecret}
								onChange={(event) => set("smtpSecret", event.target.value)}
							/>
						</>
					)}
				</>
			) : (
				<p className={styles.helper}>
					Sign-in for this provider is not wired up yet.
				</p>
			)}

			{add.isError ? (
				<InlineNotification
					kind="error"
					title="Could not add the account"
					subtitle={
						add.error instanceof Error ? add.error.message : String(add.error)
					}
					lowContrast
					hideCloseButton
				/>
			) : null}

			<Button
				disabled={
					form.providerType !== ProviderType.Imap || !imapReady || add.isPending
				}
				onClick={() => void add.mutate()}
			>
				Create account
			</Button>
		</div>
	);
}
