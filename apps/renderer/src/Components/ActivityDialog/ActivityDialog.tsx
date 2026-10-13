import { useEffect, useRef, useState } from "react";
import { Close } from "@carbon/icons-react";
import { IconButton, InlineLoading, Tag } from "@carbon/react";
import { useQuery } from "@tanstack/react-query";
import type { MailHubConnection } from "@mylomail/renderer/Shell/Backend/HubConnection";
import type {
	AccountActivityDto,
	BackgroundTaskDto,
	FolderActivityDto,
} from "@mylomail/shared-types/SignalR/MyloMail.Api.Contracts";
import { CoverageStatus } from "@mylomail/shared-types/SignalR/MyloMail.Api.Domain";
import {
	addSample,
	describeEstimate,
	estimateProgress,
	type ProgressSample,
} from "@mylomail/renderer/Lib/ProgressRate";
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
		if (dialog && !dialog.open) {
			dialog.showModal();
			// showModal focuses the first control, the close button, whose tooltip then pops up
			// unprompted. The dialog itself takes focus instead; Tab still reaches the controls.
			dialog.focus();
		}
	}, []);
	const activity = useQuery({
		queryKey: ["activity"],
		queryFn: () => hub.getActivity(),
		refetchInterval: 3_000,
	});
	const accounts = (activity.data?.accounts ?? []).filter(isBusy);
	const tasks = useQuery({
		queryKey: ["background-tasks"],
		queryFn: () => hub.getBackgroundTasks(),
		refetchInterval: 3_000,
	});
	// Ticks every second so the elapsed times move between refreshes of the list.
	const [now, setNow] = useState(() => Date.now());
	useEffect(() => {
		const timer = setInterval(() => setNow(Date.now()), 1000);
		return () => clearInterval(timer);
	}, []);
	const otherWork = tasks.data ?? [];
	// Running work first within each account, so what is happening now is what is read first.
	const tasksByAccount = new Map<string, BackgroundTaskDto[]>();
	for (const task of [...otherWork].sort(
		(a, b) => Number(b.running) - Number(a.running),
	)) {
		const key = task.accountName ?? "";
		tasksByAccount.set(key, [...(tasksByAccount.get(key) ?? []), task]);
	}

	return (
		<dialog
			ref={dialogRef}
			className={styles.dialog}
			aria-labelledby="activity-dialog-title"
			tabIndex={-1}
			onClose={onClose}
		>
			<header className={styles.header}>
				<h2 id="activity-dialog-title">Background activity</h2>
				<IconButton
					label="Close background activity"
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
				{activity.data && accounts.length === 0 && otherWork.length === 0 ? (
					<p className={styles.empty}>Nothing is running in the background.</p>
				) : null}
				{accounts.map((account) => (
					<AccountSection
						key={account.accountId}
						account={account}
						tasks={tasksByAccount.get(account.accountName) ?? []}
						now={now}
					/>
				))}
				{/* Work for an account with nothing downloading, or for no account at all. */}
				{[...tasksByAccount]
					.filter(
						([name]) =>
							!accounts.some((account) => account.accountName === name),
					)
					.map(([name, group]) => (
						<section
							key={name}
							className={styles.account}
							aria-label={name || "General"}
						>
							<div className={styles.accountHeader}>
								<h3>{name || "General"}</h3>
							</div>
							{group.map((task, index) => (
								<TaskCard
									key={`${task.label}:${index}`}
									task={task}
									now={now}
								/>
							))}
						</section>
					))}
			</div>
		</dialog>
	);
}

/** One piece of background work: what it is, whether it is under way or waiting, and how far. */
function TaskCard({
	task,
	now,
}: {
	task: BackgroundTaskDto;
	/** When the list was read, so the elapsed time is as current as the list itself. */
	now: number;
}) {
	const seconds = task.startedAt
		? Math.max(0, Math.round((now - new Date(task.startedAt).getTime()) / 1000))
		: null;
	const verb = task.running ? "Running" : "Waiting";
	return (
		<div className={`${styles.card} ${task.running ? "" : styles.waiting}`}>
			<div className={styles.cardHeader}>
				<h4>{task.label}</h4>
				{task.running ? (
					<InlineLoading status="active" description="Working" />
				) : (
					<Tag type="gray" size="sm">
						Waiting its turn
					</Tag>
				)}
			</div>
			{task.total ? (
				<>
					<progress
						className={styles.bar}
						max={task.total}
						value={task.done ?? 0}
						aria-label={task.label}
					/>
					<p className={styles.note}>
						{(task.done ?? 0).toLocaleString()} of {task.total.toLocaleString()}
					</p>
				</>
			) : null}
			{seconds !== null ? (
				<p className={styles.note}>
					{verb} for {describeElapsed(seconds)}
				</p>
			) : null}
		</div>
	);
}

/** "42 s", "5 min", "3 h 20 min", "2 d 4 h": coarse enough to read, fine enough to see it move. */
function describeElapsed(seconds: number): string {
	if (seconds < 90) return `${seconds} s`;
	const minutes = Math.round(seconds / 60);
	if (minutes < 60) return `${minutes} min`;
	const hours = Math.floor(minutes / 60);
	if (hours < 24) return `${hours} h ${minutes % 60} min`;
	return `${Math.floor(hours / 24)} d ${hours % 24} h`;
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

function AccountSection({
	account,
	tasks,
	now,
}: {
	account: AccountActivityDto;
	tasks: BackgroundTaskDto[];
	now: number;
}) {
	const contentTotal =
		account.contentReady +
		account.contentQueued +
		account.contentFetching +
		account.contentFailed;
	const contentOutstanding =
		account.contentQueued + account.contentFetching + account.contentFailed;
	const walk = account.walk;
	const walkKnownTotal =
		walk?.estimatedTotal && walk.estimatedTotal > walk.fetched
			? walk.estimatedTotal
			: null;
	const walkRate = useRate(walk?.fetched ?? 0, walkKnownTotal, "messages");
	// While the mail list is still running, content can only be downloaded for the messages it has
	// reached so far, so the account's real size is the list's total, not the content total.
	const contentExpected =
		walkKnownTotal && walkKnownTotal > contentTotal
			? walkKnownTotal
			: contentTotal;
	const contentRate = useRate(
		account.contentReady,
		contentExpected > 0 ? contentExpected : null,
		"messages",
	);

	return (
		<section className={styles.account} aria-label={account.accountName}>
			<div className={styles.accountHeader}>
				<h3>{account.accountName}</h3>
				{account.throttleRetryAfterSeconds != null ? (
					<Tag type="warm-gray" size="sm">
						Rate-limited, resumes by itself
					</Tag>
				) : null}
			</div>

			{tasks.map((task, index) => (
				<TaskCard key={`${task.label}:${index}`} task={task} now={now} />
			))}

			{walk && walk.status !== CoverageStatus.Covered ? (
				<div
					className={`${styles.card} ${walk.status === CoverageStatus.Failed ? styles.stuck : ""}`}
				>
					<div className={styles.cardHeader}>
						<h4>Step 1: finding your messages (mail list)</h4>
						<Tag
							type={walk.status === CoverageStatus.Failed ? "red" : "blue"}
							size="sm"
						>
							{statusLabel[walk.status]}
						</Tag>
					</div>
					<p className={styles.figure}>
						{walkKnownTotal
							? `${walk.fetched.toLocaleString()} of ${walkKnownTotal.toLocaleString()} messages`
							: `${walk.fetched.toLocaleString()} messages so far`}
					</p>
					{walkKnownTotal ? (
						<progress
							className={styles.bar}
							max={walkKnownTotal}
							value={walk.fetched}
						/>
					) : null}
					<p className={styles.note}>{walkRate}</p>
					<p className={styles.note}>
						Newer mail is fetched separately every minute while this runs.
						{account.stagedHistoryPages > 0
							? ` ${account.stagedHistoryPages.toLocaleString()} change${account.stagedHistoryPages === 1 ? "" : "s"} received meanwhile will be applied when it finishes.`
							: ""}
					</p>
					{walk.lastError ? (
						<p className={styles.error}>{walk.lastError}</p>
					) : null}
				</div>
			) : null}

			{account.folders.length > 0 ? (
				<div className={styles.card}>
					<div className={styles.cardHeader}>
						<h4>Folders</h4>
						<span className={styles.figure}>
							{account.folders.length} of {account.folderCount} still
							downloading
						</span>
					</div>
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

			{contentOutstanding > 0 ? (
				<div
					className={`${styles.card} ${account.contentFailed > 0 ? styles.stuck : ""}`}
				>
					<div className={styles.cardHeader}>
						<h4>Step 2: downloading each email’s text (message content)</h4>
						<span className={styles.figure}>
							{account.contentReady.toLocaleString()} of{" "}
							{contentExpected !== contentTotal ? "about " : ""}
							{contentExpected.toLocaleString()} downloaded
						</span>
					</div>
					<progress
						className={styles.bar}
						max={contentExpected}
						value={account.contentReady}
					/>
					{contentExpected !== contentTotal ? (
						<p className={styles.note}>
							Only messages the mail list has found so far can be downloaded:{" "}
							{contentTotal.toLocaleString()} of about{" "}
							{contentExpected.toLocaleString()} so far.{" "}
							{(contentExpected - contentTotal).toLocaleString()} more will
							appear here as the mail list above catches up.
						</p>
					) : null}
					<p className={styles.note}>{contentRate}</p>
					<dl className={styles.stats}>
						<Stat label="Downloaded" value={account.contentReady} />
						<Stat label="Downloading now" value={account.contentFetching} />
						<Stat label="Waiting their turn" value={account.contentQueued} />
						<Stat label="Stuck (gave up)" value={account.contentFailed} />
					</dl>
					{account.downloading.length > 0 ? (
						<>
							<p className={styles.subheading}>Downloading now</p>
							<ul className={styles.list}>
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
							<p className={styles.subheading}>Why messages are stuck</p>
							<ul className={styles.list}>
								{account.failureGroups.map((group) => (
									<li key={group.error}>
										{group.count.toLocaleString()} ×{" "}
										<span className={styles.error}>{group.error}</span>
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

function Stat({ label, value }: { label: string; value: number }) {
	return (
		<div className={styles.stat}>
			<dt>{label}</dt>
			<dd>{value.toLocaleString()}</dd>
		</div>
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
					<div className={styles.error}>{folder.lastError}</div>
				) : null}
			</td>
		</tr>
	);
}

/**
 * How fast a count has been climbing over the last few minutes and when it should finish, read
 * from the readings this dialog sees as it refreshes. Nothing is stored: closing the dialog
 * forgets them, and the figure is a guess from recent speed rather than a promise.
 */
function useRate(value: number, total: number | null, unit: string): string {
	// A clock that ticks on its own: the count only changes when work completes, so a standing
	// figure still needs time to pass for a stall to be noticed.
	const [now, setNow] = useState(() => Date.now());
	useEffect(() => {
		const timer = setInterval(() => setNow(Date.now()), 2_000);
		return () => clearInterval(timer);
	}, []);
	const [seen, setSeen] = useState<{
		value: number;
		samples: ProgressSample[];
	}>(() => ({ value, samples: [{ at: now, value }] }));
	let samples = seen.samples;
	if (seen.value !== value) {
		samples = addSample(samples, { at: now, value });
		setSeen({ value, samples });
	}
	const current = addSample(samples, { at: now, value });
	return describeEstimate(estimateProgress(current, total), unit);
}
