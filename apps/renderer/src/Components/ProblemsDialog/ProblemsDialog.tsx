import { useEffect, useRef } from "react";
import { Close } from "@carbon/icons-react";
import { Button, IconButton } from "@carbon/react";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import type { MailHubConnection } from "@mylomail/renderer/Shell/Backend/HubConnection";
import { notificationForError } from "@mylomail/renderer/Shell/Backend/ProblemDetailsTransport";
import { notify } from "@mylomail/renderer/Shell/Registries/Notifications/NotificationStore";
import { useWindowNotifications } from "@mylomail/renderer/Shell/Registries/Notifications/UseNotifications";
import {
	ProblemKind,
	type ProblemDto,
} from "@mylomail/shared-types/SignalR/MyloMail.Api.Contracts";
import styles from "@mylomail/renderer/Components/ProblemsDialog/ProblemsDialog.module.css";

/**
 * Everything background work has failed at, with the recorded error text and the affected
 * messages, so a failure is never just a number that will not go away. Failed downloads can be
 * retried; folder sync failures retry themselves on the next poll and are shown for diagnosis.
 */
export function ProblemsDialog({
	hub,
	problems,
	onClose,
	onOpenAccountSettings,
}: {
	hub: MailHubConnection;
	problems: readonly ProblemDto[];
	onClose: () => void;
	/** Opens that account's settings, where the download limit is changed. */
	onOpenAccountSettings: (accountId: string) => void;
}) {
	const dialogRef = useRef<HTMLDialogElement>(null);
	const queryClient = useQueryClient();
	const { store: notifications } = useWindowNotifications();

	// No cleanup close, for the reason given in SettingsDialog.
	useEffect(() => {
		const dialog = dialogRef.current;
		if (dialog && !dialog.open) dialog.showModal();
	}, []);

	const retry = useMutation({
		mutationFn: (messageIds: string[]) => hub.retryFailedDownloads(messageIds),
		onSuccess: () =>
			void queryClient.invalidateQueries({ queryKey: ["problems"] }),
		onError: (error: unknown) =>
			notify(
				notifications,
				notificationForError(error, "The download could not be retried"),
			),
	});

	const downloads = problems.filter(
		(problem) => problem.kind === ProblemKind.MessageDownload,
	);
	const tooLarge = problems.filter(
		(problem) => problem.kind === ProblemKind.MessageTooLarge,
	);
	const folders = problems.filter(
		(problem) => problem.kind === ProblemKind.FolderSync,
	);

	return (
		<dialog
			ref={dialogRef}
			className={styles.dialog}
			aria-labelledby="problems-dialog-title"
			onClose={onClose}
		>
			<header className={styles.header}>
				<h2 id="problems-dialog-title">Problems</h2>
				<IconButton
					label="Close problems"
					kind="ghost"
					size="md"
					align="bottom-end"
					onClick={onClose}
				>
					<Close size={20} />
				</IconButton>
			</header>
			<div className={styles.body}>
				{problems.length === 0 ? (
					<p className={styles.empty}>No problems.</p>
				) : null}

				{folders.length > 0 ? (
					<section aria-labelledby="problems-folders">
						<h3 id="problems-folders">Folders</h3>
						<ul className={styles.list}>
							{folders.map((problem) => (
								<li key={problem.id} className={styles.item}>
									<p className={styles.title}>{problem.title}</p>
									{problem.detail ? (
										<p className={styles.detail}>{problem.detail}</p>
									) : null}
									<p className={styles.note}>
										This is retried automatically on the next sync.
									</p>
								</li>
							))}
						</ul>
					</section>
				) : null}

				{tooLarge.length > 0 ? (
					<section aria-labelledby="problems-too-large">
						<h3 id="problems-too-large">
							Messages over the download limit ({tooLarge.length})
						</h3>
						<p className={styles.note}>
							These stay on the mail server until the limit is raised.
						</p>
						<ul className={styles.list}>
							{tooLarge.map((problem) => (
								<li key={problem.id} className={styles.item}>
									<div className={styles.itemHeader}>
										<p className={styles.title}>
											{problem.subject || "(no subject)"}
										</p>
										<Button
											size="sm"
											kind="tertiary"
											onClick={() => {
												onClose();
												onOpenAccountSettings(problem.accountId);
											}}
										>
											Change download limit
										</Button>
									</div>
									<p className={styles.meta}>
										{[
											problem.from,
											problem.mailboxName,
											problem.receivedAt
												? new Date(problem.receivedAt).toLocaleString()
												: null,
										]
											.filter(Boolean)
											.join(" · ")}
									</p>
									<p className={styles.detail}>{problem.detail}</p>
								</li>
							))}
						</ul>
					</section>
				) : null}

				{downloads.length > 0 ? (
					<section aria-labelledby="problems-downloads">
						<div className={styles.sectionHeader}>
							<h3 id="problems-downloads">
								Messages that could not be downloaded ({downloads.length})
							</h3>
							<Button
								size="sm"
								kind="tertiary"
								disabled={
									retry.isPending ||
									!downloads.some((problem) => problem.canRetry)
								}
								onClick={() =>
									retry.mutate(
										downloads
											.filter((problem) => problem.canRetry)
											.map((problem) => problem.messageId!),
									)
								}
							>
								Retry all
							</Button>
						</div>
						<p className={styles.note}>
							These are not searchable until they download. Each was tried
							several times before being set aside.
						</p>
						<ul className={styles.list}>
							{downloads.map((problem) => (
								<li key={problem.id} className={styles.item}>
									<div className={styles.itemHeader}>
										<p className={styles.title}>
											{problem.subject || "(no subject)"}
										</p>
										{problem.canRetry ? (
											<Button
												size="sm"
												kind="ghost"
												disabled={retry.isPending}
												onClick={() => retry.mutate([problem.messageId!])}
											>
												Retry
											</Button>
										) : null}
									</div>
									<p className={styles.meta}>
										{[
											problem.from,
											problem.mailboxName,
											problem.receivedAt
												? new Date(problem.receivedAt).toLocaleString()
												: null,
											problem.attempts ? `${problem.attempts} attempts` : null,
										]
											.filter(Boolean)
											.join(" · ")}
									</p>
									<p className={styles.detail}>
										{problem.detail ?? "No error message was recorded."}
									</p>
								</li>
							))}
						</ul>
					</section>
				) : null}
			</div>
		</dialog>
	);
}
