import { useState } from "react";
import { useMutation } from "@tanstack/react-query";
import {
	ActionableNotification,
	Button,
	InlineNotification,
	NumberInput,
	PasswordInput,
	RadioButton,
	RadioButtonGroup,
	TextInput,
	Toggle,
} from "@carbon/react";
import {
	CertificateTrustMode,
	InitialSyncMode,
	ProviderType,
} from "@mylomail/shared-types/SignalR/MyloMail.Api.Domain";
import type { AddAccountRequest } from "@mylomail/shared-types/SignalR/MyloMail.Api.Contracts";
import { ErrorCategory } from "@mylomail/shared-types/SignalR/MyloMail.Api.Errors";
import {
	present,
	type ErrorPresentation,
} from "@mylomail/renderer/Shell/Registries/Errors/ErrorPresentation";
import styles from "@mylomail/renderer/Components/AddAccount/AddAccount.module.css";

/** The subset of RFC 7807 this endpoint's failures actually carry (§15). */
interface ProblemResponse {
	title?: string;
	detail?: string;
	category?: ErrorCategory;
	extensions?: Record<string, unknown>;
}

/** Thrown with the full presentation attached, so the UI can offer more than retry-and-hope. */
class AddAccountError extends Error {
	constructor(public presentation: ErrorPresentation) {
		super(presentation.detail);
	}
}

interface FormState {
	displayName: string;
	providerType: ProviderType;
	emailAddress: string;
	secret: string;
	useOwnGoogleClient: boolean;
	gmailClientId: string;
	gmailClientSecret: string;
	host: string;
	port: number;
	useSsl: boolean;
	userName: string;
	smtpHost: string;
	smtpPort: number;
	reuseImapCredentialForSmtp: boolean;
	smtpUserName: string;
	smtpSecret: string;
	/**
	 * Set only by the "Trust this certificate and retry" action (§15) — the sole certificate
	 * trust decision available before the account exists, since pinning a specific fingerprint
	 * needs an account id to pin it against. A later, tighter pin can replace this from
	 * `AccountSettings` once the account is there to pin one for.
	 */
	trustCertificateOnRetry: boolean;
	/**
	 * Bounded (last N months/messages) or full-history initial sync (§3, §13 Epic 3) — set
	 * once, at account creation, since re-bounding an already-synced account is a different
	 * operation (backfilling further, not starting over) that this form doesn't offer.
	 */
	initialSyncMode: InitialSyncMode;
	initialSyncBoundValue: number;
}

const initial: FormState = {
	displayName: "",
	providerType: ProviderType.Imap,
	emailAddress: "",
	secret: "",
	useOwnGoogleClient: false,
	gmailClientId: "",
	gmailClientSecret: "",
	host: "",
	port: 993,
	useSsl: true,
	userName: "",
	smtpHost: "",
	smtpPort: 465,
	reuseImapCredentialForSmtp: true,
	smtpUserName: "",
	smtpSecret: "",
	trustCertificateOnRetry: false,
	initialSyncMode: InitialSyncMode.Full,
	initialSyncBoundValue: 3,
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
		mutationFn: async (trustCertificate: boolean) => {
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
				certificateTrustMode: trustCertificate
					? CertificateTrustMode.TrustAll
					: CertificateTrustMode.Default,
				initialSyncMode: form.initialSyncMode,
				initialSyncBoundValue:
					form.initialSyncMode === InitialSyncMode.Full
						? undefined
						: form.initialSyncBoundValue,
				gmailClientId:
					form.providerType === ProviderType.Gmail && form.useOwnGoogleClient
						? form.gmailClientId
						: undefined,
				gmailClientSecret:
					form.providerType === ProviderType.Gmail && form.useOwnGoogleClient
						? form.gmailClientSecret
						: undefined,
			};

			// Same-origin, so the launch cookie authenticates this without a token — the same
			// call the account list itself already makes.
			const response = await fetch("/accounts", {
				method: "POST",
				headers: { "Content-Type": "application/json" },
				body: JSON.stringify(request),
			});
			if (!response.ok) {
				const problem = (await response
					.json()
					.catch(() => null)) as ProblemResponse | null;
				const presentation =
					problem?.category !== undefined
						? present(problem.category, problem.detail, problem.extensions)
						: {
								title: problem?.title ?? "Could not add the account",
								detail:
									problem?.detail ??
									`Adding the account failed (${response.status}).`,
								transient: false as const,
							};
				throw new AddAccountError(presentation);
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
	const oauthReady = Boolean(
		form.displayName &&
		form.emailAddress &&
		(form.providerType !== ProviderType.Gmail ||
			!form.useOwnGoogleClient ||
			(form.gmailClientId && form.gmailClientSecret)),
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
					labelText="Google"
					value={String(ProviderType.Gmail)}
				/>
				<RadioButton
					id="provider-microsoft365"
					labelText="Microsoft 365"
					value={String(ProviderType.Microsoft365)}
				/>
			</RadioButtonGroup>

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
			{form.providerType === ProviderType.Imap ? (
				<>
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
				<>
					<p className={styles.helper}>
						A browser window will open for secure provider sign-in.
					</p>
					{form.providerType === ProviderType.Gmail ? (
						<>
							<Toggle
								id="add-account-google-byoc"
								labelText="Use your own Google OAuth client"
								toggled={form.useOwnGoogleClient}
								onToggle={(checked) => set("useOwnGoogleClient", checked)}
							/>
							{form.useOwnGoogleClient ? (
								<>
									<TextInput
										id="add-account-google-client-id"
										labelText="Google OAuth client ID"
										value={form.gmailClientId}
										onChange={(event) =>
											set("gmailClientId", event.target.value)
										}
									/>
									<PasswordInput
										id="add-account-google-client-secret"
										labelText="Google OAuth client secret"
										value={form.gmailClientSecret}
										onChange={(event) =>
											set("gmailClientSecret", event.target.value)
										}
									/>
									<p className={styles.helper}>
										Create a Desktop app OAuth client, enable the Gmail, Google
										Calendar, and People APIs, and publish the consent screen
										for normal use. Projects left in Testing issue refresh
										tokens that expire after about seven days.
									</p>
								</>
							) : null}
						</>
					) : null}
				</>
			)}

			<RadioButtonGroup
				legendText="Initial sync"
				name="initial-sync-mode"
				valueSelected={String(form.initialSyncMode)}
				onChange={(value) =>
					set("initialSyncMode", Number(value) as InitialSyncMode)
				}
			>
				<RadioButton
					id="initial-sync-full"
					labelText="Full history"
					value={String(InitialSyncMode.Full)}
				/>
				<RadioButton
					id="initial-sync-months"
					labelText="Last N months"
					value={String(InitialSyncMode.LastNMonths)}
				/>
				<RadioButton
					id="initial-sync-messages"
					labelText="Last N messages"
					value={String(InitialSyncMode.LastNMessages)}
				/>
			</RadioButtonGroup>
			{form.initialSyncMode === InitialSyncMode.Full ? null : (
				<NumberInput
					id="initial-sync-bound"
					label={
						form.initialSyncMode === InitialSyncMode.LastNMonths
							? "Months"
							: "Messages"
					}
					min={1}
					value={form.initialSyncBoundValue}
					onChange={(_, { value }) =>
						set("initialSyncBoundValue", Number(value))
					}
				/>
			)}
			{form.providerType === ProviderType.Microsoft365 &&
			form.initialSyncMode !== InitialSyncMode.Full ? (
				// §3: a count/date bound isn't a stable Graph delta predicate, so a bounded
				// initial sync here only limits what gets materialised locally sooner — the
				// full mailbox is still walked in the background before incremental sync can
				// start. Stated here so "last 3 months" doesn't imply a speed benefit this
				// provider won't deliver.
				<p className={styles.helper}>
					For Microsoft 365, this limits what appears locally at first, not how
					much of your mailbox is scanned — the full account is still walked in
					the background either way.
				</p>
			) : null}

			{add.isError && add.error instanceof AddAccountError ? (
				add.error.presentation.action === "trust-certificate" &&
				add.error.presentation.certificate ? (
					<ActionableNotification
						kind="warning"
						title={add.error.presentation.title}
						subtitle={`${add.error.presentation.detail} Only continue if you recognise and trust ${add.error.presentation.certificate.hostname}.`}
						actionButtonLabel="Trust this certificate and retry"
						onActionButtonClick={() => {
							set("trustCertificateOnRetry", true);
							add.mutate(true);
						}}
						lowContrast
						hideCloseButton
						inline
					/>
				) : (
					<InlineNotification
						kind="error"
						title={add.error.presentation.title}
						subtitle={add.error.presentation.detail}
						lowContrast
						hideCloseButton
					/>
				)
			) : add.isError ? (
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
					add.isPending ||
					(form.providerType === ProviderType.Imap ? !imapReady : !oauthReady)
				}
				onClick={() => void add.mutate(form.trustCertificateOnRetry)}
			>
				Create account
			</Button>
		</div>
	);
}
