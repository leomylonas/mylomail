import { useEffect, useRef, useState } from "react";
import { Add, Close } from "@carbon/icons-react";
import { IconButton } from "@carbon/react";
import type { MailHubConnection } from "@mylomail/renderer/Shell/Backend/HubConnection";
import {
	AccountSettings,
	type AccountSettingsValues,
} from "@mylomail/renderer/Components/AccountSettings/AccountSettings";
import { AddAccount } from "@mylomail/renderer/Components/AddAccount/AddAccount";
import { ShellSettings } from "@mylomail/renderer/Components/ShellSettings/ShellSettings";
import type { Account } from "@mylomail/renderer/Types/Account";
import {
	CertificateTrustMode,
	InitialSyncMode,
	ProviderType,
} from "@mylomail/shared-types/SignalR/MyloMail.Api.Domain";
import styles from "@mylomail/renderer/Components/SettingsDialog/SettingsDialog.module.css";

export type SettingsSection =
	| { kind: "general" }
	| { kind: "account"; accountId: string }
	| { kind: "add-account" };

/**
 * Every settings surface in one modal, the way Outlook presents them: app-wide settings,
 * one page per account, and the add-account form, chosen from a list on the left. Settings
 * never occupy the reading pane, which belongs to the message being read.
 */
export function SettingsDialog({
	hub,
	accounts,
	initialSection,
	onClose,
	onAccountAdded,
	onAccountRemoved,
}: {
	hub: MailHubConnection | null;
	accounts: Account[];
	initialSection: SettingsSection;
	onClose: () => void;
	onAccountAdded: () => void;
	onAccountRemoved: (accountId: string) => void;
}) {
	const dialogRef = useRef<HTMLDialogElement>(null);
	const [section, setSection] = useState(initialSection);

	// `showModal` puts the dialog in the top layer with a focus trap and Escape handling.
	// No cleanup close: unmounting removes it, and a `close()` here would fire a `close`
	// event that reads as the user dismissing a dialog StrictMode merely remounted.
	useEffect(() => {
		const dialog = dialogRef.current;
		if (dialog && !dialog.open) dialog.showModal();
	}, []);

	const activeAccount =
		section.kind === "account"
			? accounts.find((account) => account.id === section.accountId)
			: undefined;

	return (
		<dialog
			ref={dialogRef}
			className={styles.dialog}
			aria-labelledby="settings-dialog-title"
			onClose={onClose}
			onCancel={(event) => {
				// A confirmation modal nested in a settings page owns Escape while it is open.
				if (event.currentTarget.querySelector(".cds--modal.is-visible"))
					event.preventDefault();
			}}
		>
			<header className={styles.header}>
				<h2 id="settings-dialog-title">Settings</h2>
				<IconButton
					label="Close settings"
					kind="ghost"
					size="md"
					align="bottom-end"
					onClick={onClose}
				>
					<Close size={20} />
				</IconButton>
			</header>
			<div className={styles.layout}>
				<nav className={styles.nav} aria-label="Settings sections">
					<NavItem
						selected={section.kind === "general"}
						onClick={() => setSection({ kind: "general" })}
					>
						General
					</NavItem>
					<span className={styles.navGroup}>Accounts</span>
					{accounts.map((account) => (
						<NavItem
							key={account.id}
							selected={
								section.kind === "account" && section.accountId === account.id
							}
							onClick={() =>
								setSection({ kind: "account", accountId: account.id })
							}
						>
							<span
								className={styles.swatch}
								style={{
									backgroundColor: account.color || "var(--cds-icon-secondary)",
								}}
								aria-hidden="true"
							/>
							<span className={styles.navLabel}>{account.displayName}</span>
						</NavItem>
					))}
					<NavItem
						selected={section.kind === "add-account"}
						onClick={() => setSection({ kind: "add-account" })}
					>
						<Add size={16} aria-hidden="true" />
						<span className={styles.navLabel}>Add account</span>
					</NavItem>
				</nav>
				<div className={styles.content}>
					{section.kind === "general" ? <ShellSettings /> : null}
					{section.kind === "account" && hub && activeAccount ? (
						<AccountSettings
							key={activeAccount.id}
							hub={hub}
							initial={toSettings(activeAccount)}
							isThrottled={activeAccount.isThrottled}
							onRemoved={() => {
								setSection({ kind: "general" });
								onAccountRemoved(activeAccount.id);
							}}
						/>
					) : null}
					{section.kind === "add-account" ? (
						<AddAccount
							onAdded={() => {
								onAccountAdded();
								onClose();
							}}
						/>
					) : null}
				</div>
			</div>
		</dialog>
	);
}

function NavItem({
	selected,
	onClick,
	children,
}: {
	selected: boolean;
	onClick: () => void;
	children: React.ReactNode;
}) {
	return (
		<button
			type="button"
			className={`${styles.navItem} ${selected ? styles.navItemSelected : ""}`}
			aria-current={selected ? "page" : undefined}
			onClick={onClick}
		>
			{children}
		</button>
	);
}

/** The settings form's starting values, from the account list the shell already holds. */
function toSettings(account: Account): AccountSettingsValues {
	return {
		id: account.id,
		displayName: account.displayName,
		color: account.color ?? "",
		pollIntervalSeconds: account.pollIntervalSeconds ?? 60,
		pollingEnabled: account.pollingEnabled ?? true,
		undoSendDelaySeconds: account.undoSendDelaySeconds ?? 0,
		notificationsEnabled: account.notificationsEnabled ?? true,
		initialSyncMode: account.initialSyncMode ?? InitialSyncMode.Full,
		initialSyncBoundValue: account.initialSyncBoundValue ?? null,
		certificateTrustMode:
			account.certificateTrustMode ?? CertificateTrustMode.Default,
		attachmentSizeLimitOverride: account.attachmentSizeLimitOverride ?? null,
		maxMessageDownloadMegabytes: account.maxMessageDownloadMegabytes ?? 128,
		groupConversations: account.groupConversations ?? false,
		providerType: account.providerType ?? ProviderType.Imap,
		appendToSentOnSend: account.appendToSentOnSend ?? null,
	};
}
