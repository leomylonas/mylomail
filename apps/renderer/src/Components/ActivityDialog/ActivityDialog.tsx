import { useEffect, useRef } from "react";
import { Close } from "@carbon/icons-react";
import { IconButton } from "@carbon/react";
import { useQuery } from "@tanstack/react-query";
import type { MailHubConnection } from "@mylomail/renderer/Shell/Backend/HubConnection";
import type {
	AccountActivityDto,
	FolderActivityDto,
} from "@mylomail/shared-types/SignalR/MyloMail.Api.Contracts";
import { CoverageStatus } from "@mylomail/shared-types/SignalR/MyloMail.Api.Domain";
import styles from "@mylomail/renderer/Components/ActivityDialog/ActivityDialog.module.css";

const statusLabel: Record<CoverageStatus, string> = {
	[CoverageStatus.NotStarted]: "Waiting",
	[CoverageStatus.Backfilling]: "Downloading",
	[CoverageStatus.Covered]: "Done",
	[CoverageStatus.Failed]: "Stuck",
};

/**
 * Exactly what background downloading is doing, per account: what is being downloaded now,
 * what is queued, and what is stuck and why. Refreshes while open.
 */
export function ActivityDialog({
	hub,
	onClose,
}: {
	hub: MailHubConnection;
	onClose: () => void;
}) {
	const dialogRef = useRef<HTMLDialogElement>(null);
	useEffect(() => {
		const dialog = dialogRef.current;
		if (dialog && !dialog.open) dialog.showModal();
	}, []);
	const activity = useQuery({
		queryKey: ["activity"],
		queryFn: () => hub.getActivity(),
		refetchInterval: 3_000,
	});
	const accounts = (activity.data?.accounts ?? []).filter(isBusy);

	return (
		<dialog
			ref={dialogRef}
			className={styles.dialog}
			aria-labelledby="activity-dialog-title"
			onClose={onClose}
		>
			<header className={styles.header}>
				<h2 id="activity-dialog-title">Downloads</h2>
				<IconButton
					label="Close downloads"
					kind="ghost"
					size="md"
					align="bottom-end"
					onClick={onClose}
				>
					<Close size={20} />
				</IconButton>
			</header>
			<div className={styles.body}>
				{activity.isPending ? <p className={styles.empty}>Loading…</p> : null}
				{activity.data && accounts.length === 0 ? (
					<p className={styles.empty}>Nothing is downloading.</p>
				) : null}
				{accounts.map((account) => (
					<AccountSection key={account.accountId} account={account} />
				))}
			</div>
		</dialog>
	);
}

function isBusy(account: AccountActivityDto): boolean {
	return (
		(account.walk != null && account.walk.status !== CoverageStatus.Covered) ||
		account.folders.length > 0 ||
		account.contentQueued + account.contentFetching + account.contentFailed >
			0 ||
		account.stagedHistoryPages > 0 ||
		account.throttleRetryAfterSeconds != null
	);
}

function AccountSection({ account }: { account: AccountActivityDto }) {
	const contentTotal =
		account.contentReady +
		account.contentQueued +
		account.contentFetching +
		account.contentFailed;
	const walk = account.walk;
	return (
		<section aria-label={account.accountName}>
			<h3>
				{account.accountName}
				{account.throttleRetryAfterSeconds != null ? (
					<span className={styles.badge}>
						rate-limited by the provider; resumes by itself
					</span>
				) : null}
			</h3>

			{walk && walk.status !== CoverageStatus.Covered ? (
				<div
					className={`${styles.item} ${walk.status === CoverageStatus.Failed ? styles.stuck : ""}`}
				>
					<p className={styles.title}>Mail list: {statusLabel[walk.status]}</p>
					<p className={styles.meta}>
						{walk.estimatedTotal && walk.estimatedTotal > walk.fetched
							? `${walk.fetched.toLocaleString()} of ${walk.estimatedTotal.toLocaleString()} messages`
							: `${walk.fetched.toLocaleString()} messages so far`}
						. Newer mail is fetched separately every minute while this runs.
					</p>
					{walk.estimatedTotal && walk.estimatedTotal > walk.fetched ? (
						<progress
							className={styles.bar}
							max={walk.estimatedTotal}
							value={walk.fetched}
						/>
					) : null}
					{walk.lastError ? (
						<p className={styles.detail}>{walk.lastError}</p>
					) : null}
					{account.stagedHistoryPages > 0 ? (
						<p className={styles.note}>
							{account.stagedHistoryPages.toLocaleString()} change
							{account.stagedHistoryPages === 1 ? "" : "s"} received while
							downloading are held and applied when it finishes.
						</p>
					) : null}
				</div>
			) : null}

			{account.folders.length > 0 ? (
				<div className={styles.item}>
					<p className={styles.title}>
						Folders: {account.folders.length} of {account.folderCount} still
						downloading
					</p>
					<table className={styles.table}>
						<thead>
							<tr>
								<th>Folder</th>
								<th>State</th>
								<th>Progress</th>
							</tr>
						</thead>
						<tbody>
							{account.folders.map((folder) => (
								<FolderRow key={folder.name} folder={folder} />
							))}
						</tbody>
					</table>
				</div>
			) : null}

			{contentTotal > 0 &&
			account.contentQueued + account.contentFetching + account.contentFailed >
				0 ? (
				<div
					className={`${styles.item} ${account.contentFailed > 0 ? styles.stuck : ""}`}
				>
					<p className={styles.title}>Message content</p>
					<p className={styles.stats}>
						<span>
							<strong>{account.contentReady.toLocaleString()}</strong>{" "}
							downloaded
						</span>
						<span>
							<strong>{account.contentFetching.toLocaleString()}</strong>{" "}
							downloading now
						</span>
						<span>
							<strong>{account.contentQueued.toLocaleString()}</strong> queued
						</span>
						<span>
							<strong>{account.contentFailed.toLocaleString()}</strong> stuck
						</span>
					</p>
					<progress
						className={styles.bar}
						max={contentTotal}
						value={account.contentReady}
					/>
					{account.downloading.length > 0 ? (
						<>
							<p className={styles.note}>Downloading now:</p>
							<ul>
								{account.downloading.map((message) => (
									<li key={message.messageId}>
										{message.subject || "(no subject)"}
										{message.from ? ` — ${message.from}` : ""}
										{message.attempts > 0
											? ` (attempt ${message.attempts + 1})`
											: ""}
									</li>
								))}
							</ul>
						</>
					) : null}
					{account.failureGroups.length > 0 ? (
						<>
							<p className={styles.note}>Why messages are stuck:</p>
							<ul>
								{account.failureGroups.map((group) => (
									<li key={group.error}>
										{group.count.toLocaleString()} ×{" "}
										<span className={styles.detail}>{group.error}</span>
									</li>
								))}
							</ul>
						</>
					) : null}
				</div>
			) : null}
		</section>
	);
}

function FolderRow({ folder }: { folder: FolderActivityDto }) {
	return (
		<tr>
			<td>{folder.name}</td>
			<td>{statusLabel[folder.status]}</td>
			<td>
				{folder.estimatedTotal
					? `${folder.fetched.toLocaleString()} of ${folder.estimatedTotal.toLocaleString()}`
					: folder.fetched.toLocaleString()}
				{folder.lastError ? (
					<span className={styles.detail}> {folder.lastError}</span>
				) : null}
			</td>
		</tr>
	);
}
