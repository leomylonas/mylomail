import { useState } from "react";
import { useMutation } from "@tanstack/react-query";
import {
	ActionableNotification,
	Button,
	InlineNotification,
	NumberInput,
	Select,
	SelectItem,
	PasswordInput,
	RadioButton,
	RadioButtonGroup,
	TextInput,
	Toggle,
} from "@carbon/react";
import {
	CertificateTrustMode,
	ImapAuthMethod,
	InitialSyncMode,
	MailTransportSecurity,
	ProviderType,
	SmtpAuthMethod,
} from "@mylomail/shared-types/SignalR/MyloMail.Api.Domain";
import type { AddAccountRequest } from "@mylomail/shared-types/SignalR/MyloMail.Api.Contracts";
import {
	fetchApi,
	MutationTransportError,
} from "@mylomail/renderer/Shell/Backend/ProblemDetailsTransport";
import styles from "@mylomail/renderer/Components/AddAccount/AddAccount.module.css";

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
	imapSecurity: MailTransportSecurity;
	imapAuthMethod: ImapAuthMethod;
	userName: string;
	smtpHost: string;
	smtpPort: number;
	reuseImapCredentialForSmtp: boolean;
	smtpSecurity: MailTransportSecurity;
	smtpAuthMethod: SmtpAuthMethod;
	smtpUserName: string;
	smtpSecret: string;
	enableCalDav: boolean;
	calDavEndpoint: string;
	calDavUserName: string;
	reuseImapCredentialForCalDav: boolean;
	calDavSecret: string;
	/**
	 * Set only by the "Accept all certificates and retry" action (§15) — the sole certificate
	 * trust decision available before the account exists, since pinning a specific fingerprint
	 * needs an account id to pin it against. A later, tighter pin can replace this from
	 * `AccountSettings` once the account is there to pin one for.
	 */
	trustCertificateOnRetry: boolean;
	/**
	 * Bounded (last N months/messages) or full-history initial sync (§3, §13 Epic 3).
	 * `AccountSettings` can change this later and atomically restart inherited mailbox
	 * coverage without discarding messages already materialised.
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
	imapSecurity: MailTransportSecurity.TlsOnConnect,
	imapAuthMethod: ImapAuthMethod.Password,
	userName: "",
	smtpHost: "",
	smtpPort: 465,
	reuseImapCredentialForSmtp: true,
	smtpSecurity: MailTransportSecurity.StartTls,
	smtpAuthMethod: SmtpAuthMethod.Password,
	smtpUserName: "",
	smtpSecret: "",
	enableCalDav: false,
	calDavEndpoint: "",
	calDavUserName: "",
	reuseImapCredentialForCalDav: true,
	calDavSecret: "",
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
					imapSecurity: form.imapSecurity,
					authMethod: form.imapAuthMethod,
					userName: form.userName || form.emailAddress,
					smtpHost: form.smtpHost,
					smtpPort: form.smtpPort,
					smtpSecurity: form.smtpSecurity,
					smtpAuthMethod: form.smtpAuthMethod,
					reuseImapCredentialForSmtp: form.reuseImapCredentialForSmtp,
					smtpUserName: form.reuseImapCredentialForSmtp
						? undefined
						: form.smtpUserName,
					smtpSecret: form.reuseImapCredentialForSmtp
						? undefined
						: form.smtpSecret,
				},
				calDav:
					form.providerType === ProviderType.Imap && form.enableCalDav
						? {
								endpoint: form.calDavEndpoint,
								userName: form.calDavUserName || form.emailAddress,
								reuseImapCredential: form.reuseImapCredentialForCalDav,
								secret: form.reuseImapCredentialForCalDav
									? undefined
									: form.calDavSecret,
							}
						: undefined,
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
			await fetchApi("/accounts", {
				method: "POST",
				headers: { "Content-Type": "application/json" },
				body: JSON.stringify(request),
			});
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
		form.smtpHost &&
		(form.imapAuthMethod !== ImapAuthMethod.Password ||
			form.imapSecurity !== MailTransportSecurity.None) &&
		(form.smtpAuthMethod === SmtpAuthMethod.None ||
			(form.smtpSecurity !== MailTransportSecurity.None &&
				(form.reuseImapCredentialForSmtp
					? (form.imapAuthMethod === ImapAuthMethod.OAuth2) ===
						(form.smtpAuthMethod === SmtpAuthMethod.OAuth2)
					: Boolean(form.smtpUserName && form.smtpSecret)))) &&
		(!form.enableCalDav ||
			(form.calDavEndpoint &&
				(form.reuseImapCredentialForCalDav
					? form.imapAuthMethod !== ImapAuthMethod.OAuth2
					: Boolean(form.calDavSecret)))),
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
					<Select
						id="add-account-imap-security"
						labelText="IMAP security"
						value={String(form.imapSecurity)}
						onChange={(event) =>
							set(
								"imapSecurity",
								Number(event.target.value) as MailTransportSecurity,
							)
						}
					>
						<SelectItem
							value={String(MailTransportSecurity.TlsOnConnect)}
							text="TLS on connect"
						/>
						<SelectItem
							value={String(MailTransportSecurity.StartTls)}
							text="STARTTLS (required)"
						/>
						<SelectItem
							value={String(MailTransportSecurity.None)}
							text="No encryption"
						/>
					</Select>
					<Select
						id="add-account-imap-auth"
						labelText="IMAP authentication"
						value={String(form.imapAuthMethod)}
						onChange={(event) =>
							set(
								"imapAuthMethod",
								Number(event.target.value) as ImapAuthMethod,
							)
						}
					>
						<SelectItem
							value={String(ImapAuthMethod.Password)}
							text="Password"
						/>
						<SelectItem
							value={String(ImapAuthMethod.OAuth2)}
							text="OAuth 2 access token"
						/>
					</Select>
					<TextInput
						id="add-account-username"
						labelText="Login name"
						helperText="Leave blank to use the email address."
						value={form.userName}
						onChange={(event) => set("userName", event.target.value)}
					/>
					<PasswordInput
						id="add-account-secret"
						labelText={
							form.imapAuthMethod === ImapAuthMethod.Password
								? "Password"
								: "OAuth 2 access token"
						}
						value={form.secret}
						onChange={(event) => set("secret", event.target.value)}
					/>
					{form.imapAuthMethod === ImapAuthMethod.Password &&
					form.imapSecurity === MailTransportSecurity.None ? (
						<p className={styles.helper}>
							Password authentication requires TLS on connect or mandatory
							STARTTLS.
						</p>
					) : null}

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
					<Select
						id="add-account-smtp-security"
						labelText="SMTP security"
						value={String(form.smtpSecurity)}
						onChange={(event) =>
							set(
								"smtpSecurity",
								Number(event.target.value) as MailTransportSecurity,
							)
						}
					>
						<SelectItem
							value={String(MailTransportSecurity.TlsOnConnect)}
							text="TLS on connect"
						/>
						<SelectItem
							value={String(MailTransportSecurity.StartTls)}
							text="STARTTLS (required)"
						/>
						<SelectItem
							value={String(MailTransportSecurity.None)}
							text="No encryption"
						/>
					</Select>
					<Select
						id="add-account-smtp-auth"
						labelText="SMTP authentication"
						value={String(form.smtpAuthMethod)}
						onChange={(event) =>
							set(
								"smtpAuthMethod",
								Number(event.target.value) as SmtpAuthMethod,
							)
						}
					>
						<SelectItem value={String(SmtpAuthMethod.None)} text="None" />
						<SelectItem
							value={String(SmtpAuthMethod.Password)}
							text="Password"
						/>
						<SelectItem
							value={String(SmtpAuthMethod.OAuth2)}
							text="OAuth 2 access token"
						/>
					</Select>
					{form.smtpAuthMethod === SmtpAuthMethod.None ? null : (
						<>
							<Toggle
								id="add-account-smtp-reuse"
								labelText="Use the IMAP credential for sending"
								toggled={form.reuseImapCredentialForSmtp}
								onToggle={(checked) =>
									set("reuseImapCredentialForSmtp", checked)
								}
							/>
							{form.reuseImapCredentialForSmtp ? null : (
								<>
									<TextInput
										id="add-account-smtp-username"
										labelText="SMTP login name"
										value={form.smtpUserName}
										onChange={(event) =>
											set("smtpUserName", event.target.value)
										}
									/>
									<PasswordInput
										id="add-account-smtp-secret"
										labelText={
											form.smtpAuthMethod === SmtpAuthMethod.Password
												? "SMTP password"
												: "SMTP OAuth 2 access token"
										}
										value={form.smtpSecret}
										onChange={(event) => set("smtpSecret", event.target.value)}
									/>
								</>
							)}
						</>
					)}
					{form.smtpAuthMethod !== SmtpAuthMethod.None &&
					form.smtpSecurity === MailTransportSecurity.None ? (
						<p className={styles.helper}>
							SMTP authentication requires TLS on connect or mandatory STARTTLS.
						</p>
					) : null}
					<Toggle
						id="add-account-caldav"
						labelText="Add a CalDAV calendar"
						toggled={form.enableCalDav}
						onToggle={(checked) => set("enableCalDav", checked)}
					/>
					{form.enableCalDav ? (
						<>
							<TextInput
								id="add-account-caldav-endpoint"
								labelText="CalDAV endpoint"
								helperText="Use the absolute HTTPS calendar endpoint supplied by your provider."
								type="url"
								value={form.calDavEndpoint}
								onChange={(event) => set("calDavEndpoint", event.target.value)}
							/>
							<TextInput
								id="add-account-caldav-username"
								labelText="CalDAV login name"
								helperText="Leave blank to use the email address."
								value={form.calDavUserName}
								onChange={(event) => set("calDavUserName", event.target.value)}
							/>
							<Toggle
								id="add-account-caldav-reuse"
								labelText="Use the IMAP password for CalDAV"
								toggled={form.reuseImapCredentialForCalDav}
								onToggle={(checked) =>
									set("reuseImapCredentialForCalDav", checked)
								}
							/>
							{form.reuseImapCredentialForCalDav &&
							form.imapAuthMethod === ImapAuthMethod.OAuth2 ? (
								<p className={styles.helper}>
									CalDAV Basic authentication needs an independent password; it
									cannot reuse an IMAP OAuth 2 token.
								</p>
							) : null}
							{form.reuseImapCredentialForCalDav ? null : (
								<PasswordInput
									id="add-account-caldav-secret"
									labelText="CalDAV password"
									value={form.calDavSecret}
									onChange={(event) => set("calDavSecret", event.target.value)}
								/>
							)}
						</>
					) : null}
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

			{add.isError && add.error instanceof MutationTransportError ? (
				add.error.presentation.action === "trust-certificate" &&
				add.error.presentation.certificate ? (
					<ActionableNotification
						kind="warning"
						title={add.error.presentation.title}
						subtitle={`${add.error.presentation.detail} Continuing disables certificate verification for all IMAP, SMTP, and CalDAV connections on this account—not only ${add.error.presentation.certificate.hostname}. Only continue if you accept that account-wide security downgrade.`}
						actionButtonLabel="Accept all certificates and retry"
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
