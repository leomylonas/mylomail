import { useEffect, useReducer, useState } from "react";
import { WarningAltFilled } from "@carbon/icons-react";
import { Tooltip } from "@carbon/react";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import type { MailHubConnection } from "@mylomail/renderer/Shell/Backend/HubConnection";
import { ActivityDialog } from "@mylomail/renderer/Components/ActivityDialog/ActivityDialog";
import { ProblemsDialog } from "@mylomail/renderer/Components/ProblemsDialog/ProblemsDialog";
import {
	ProblemKind,
	type SyncProgressDto,
} from "@mylomail/shared-types/SignalR/MyloMail.Api.Contracts";
import {
	CoverageStatus,
	SyncProgressKind,
} from "@mylomail/shared-types/SignalR/MyloMail.Api.Domain";
import styles from "@mylomail/renderer/Components/StatusBar/StatusBar.module.css";

interface MailboxProgressSeed {
	id: string;
	accountId?: string;
	coverage: CoverageStatus;
	coverageMessagesFetched: number;
	coverageEstimatedTotal: number | null;
	providerTotalCount: number | null;
	localCount: number;
	isSynthesized: boolean;
}

export interface BackgroundActivity {
	syncing: { fetched: number; total: number | null; folders: number } | null;
}

/**
 * Where background and long-running work is reported: initial mail download and search
 * indexing, plus a broken backend connection. Reads the same cache-only progress entries the
 * hub writes (§13 Epic 3) rather than asking the server, so it costs nothing while idle.
 */
export function StatusBar({
	hub,
	problem,
	onOpenAccountSettings,
}: {
	hub: MailHubConnection | null;
	problem: string | null;
	onOpenAccountSettings: (accountId: string) => void;
}) {
	const queryClient = useQueryClient();
	const [problemsOpen, setProblemsOpen] = useState(false);
	const [activityOpen, setActivityOpen] = useState(false);
	// Failures are recorded durably by background work; polling picks up those that raise no
	// event of their own (a message given up on), while account and folder changes invalidate.
	const problems = useQuery({
		queryKey: ["problems"],
		queryFn: () => hub!.getProblems(),
		enabled: hub !== null,
		refetchInterval: 15_000,
	});
	// The download queue is counted on the server across every message, rather than assembled
	// from per-folder events, which only ever covered folders that had reported this session.
	const queue = useQuery({
		queryKey: ["content-queue"],
		queryFn: () => hub!.getContentQueue(),
		enabled: hub !== null,
		refetchInterval: (query) =>
			query.state.data &&
			query.state.data.waiting + query.state.data.fetching > 0
				? 3_000
				: 15_000,
	});
	// Provider rate limits are informational and clear on their own, so they are a muted note in
	// the activity area rather than an entry in the problems list. Polled while one is active so
	// the countdown and its disappearance are both seen.
	const throttled = useQuery({
		queryKey: ["throttled-accounts"],
		queryFn: () => hub!.getThrottledAccounts(),
		enabled: hub !== null,
		refetchInterval: (query) => (query.state.data?.length ? 3_000 : 10_000),
	});
	const pendingContent =
		(queue.data?.waiting ?? 0) + (queue.data?.fetching ?? 0);
	const totalContent = pendingContent + (queue.data?.ready ?? 0);
	const retrying =
		problems.data?.filter((p) => p.kind === ProblemKind.MessageRetrying)
			.length ?? 0;
	const problemList =
		problems.data?.filter((p) => p.kind !== ProblemKind.MessageRetrying) ?? [];
	const problemCount = problemList.length;
	const [, refresh] = useReducer((count: number) => count + 1, 0);
	useEffect(
		() =>
			queryClient.getQueryCache().subscribe((event) => {
				const key = event.query.queryKey[0];
				if (key === "sync-progress" || key === "mailboxes") refresh();
			}),
		[queryClient],
	);

	const cache = queryClient.getQueryCache();
	const progress = cache
		.findAll({ queryKey: ["sync-progress"] })
		.map((query) => query.state.data as SyncProgressDto | undefined)
		.filter((entry): entry is SyncProgressDto => entry !== undefined);
	const mailboxes = cache
		.findAll({ queryKey: ["mailboxes"] })
		.flatMap(
			(query) => (query.state.data as MailboxProgressSeed[] | undefined) ?? [],
		);
	const activity = summariseActivity(progress, mailboxes);

	return (
		<footer className={styles.statusBar} aria-label="Status">
			{pendingContent > 0 ? (
				<progress
					className={styles.progress}
					max={totalContent}
					value={queue.data?.ready ?? 0}
					aria-label="Message content downloaded"
				/>
			) : null}
			<div className={styles.activity} role="status" aria-live="polite">
				{activity.syncing || pendingContent > 0 || retrying > 0 ? (
					<Tooltip
						align="top"
						label={
							<>
								{activity.syncing ? (
									<div>
										Downloading mail
										{activity.syncing.folders > 1
											? ` in ${activity.syncing.folders} folders`
											: ""}
										:{" "}
										{activity.syncing.total
											? `${activity.syncing.fetched.toLocaleString()} of ${activity.syncing.total.toLocaleString()}`
											: `${activity.syncing.fetched.toLocaleString()} so far`}
									</div>
								) : null}
								{pendingContent > 0 ? (
									<div>
										Downloading message content:{" "}
										{(queue.data?.ready ?? 0).toLocaleString()} of{" "}
										{totalContent.toLocaleString()} (
										{pendingContent.toLocaleString()} queued)
									</div>
								) : null}
								{retrying > 0 ? (
									<div>
										Retrying {retrying} failed{" "}
										{retrying === 1 ? "download" : "downloads"}
									</div>
								) : null}
								<div>Click for details.</div>
							</>
						}
					>
						<button
							type="button"
							className={styles.activityButton}
							aria-haspopup="dialog"
							onClick={() => setActivityOpen(true)}
						>
							Syncing…
						</button>
					</Tooltip>
				) : null}
				{throttled.data?.length ? (
					<Tooltip
						align="top"
						label={
							<>
								{throttled.data.map((t) => (
									<div key={t.accountId}>
										{t.accountName} is rate-limited by its provider.
									</div>
								))}
								<div>
									Nothing is wrong: the provider asked MyloMail to slow down,
									and syncing resumes by itself within a minute or so.
								</div>
							</>
						}
					>
						<button type="button" className={styles.throttle}>
							Throttled
						</button>
					</Tooltip>
				) : null}
				{!activity.syncing &&
				pendingContent === 0 &&
				retrying === 0 &&
				!throttled.data?.length ? (
					<span className={styles.idle}>Up to date</span>
				) : null}
			</div>
			<div className={styles.right}>
				{problem ? <span className={styles.problem}>{problem}</span> : null}
				{problemCount > 0 ? (
					<button
						type="button"
						className={styles.problemsButton}
						aria-haspopup="dialog"
						onClick={() => setProblemsOpen(true)}
					>
						<WarningAltFilled size={16} aria-hidden="true" />
						{problemCount} {problemCount === 1 ? "problem" : "problems"}
					</button>
				) : null}
			</div>
			{activityOpen && hub ? (
				<ActivityDialog hub={hub} onClose={() => setActivityOpen(false)} />
			) : null}
			{problemsOpen && hub ? (
				<ProblemsDialog
					hub={hub}
					problems={problemList}
					onClose={() => setProblemsOpen(false)}
					onOpenAccountSettings={onOpenAccountSettings}
				/>
			) : null}
		</footer>
	);
}

/**
 * Totals download progress across every real folder, and indexing progress across the folders
 * that still have unindexed messages.
 *
 * Downloading is shown for as long as any folder is still to be fetched — `Backfilling` or
 * `NotStarted`. Folders are fetched one after another, so counting only `Backfilling` ones made
 * the indicator vanish in the gap between folders. Finished folders count as fully done, so the
 * figure only ever climbs. A live event refines a folder's position over its durable summary.
 */
export function summariseActivity(
	progress: readonly SyncProgressDto[],
	mailboxes: readonly MailboxProgressSeed[],
): BackgroundActivity {
	const live = new Map<string, SyncProgressDto>();
	for (const entry of progress)
		if (entry.kind === SyncProgressKind.Coverage)
			live.set(entry.mailboxId, entry);

	const real = mailboxes.filter((mailbox) => !mailbox.isSynthesized);
	const pending = real.filter(
		(mailbox) =>
			mailbox.coverage === CoverageStatus.Backfilling ||
			mailbox.coverage === CoverageStatus.NotStarted,
	);
	// Only accounts that still have something to download. Folders of an account that is entirely
	// done would otherwise swamp the figure and show "all downloaded" while another account has
	// barely started; within an account that is still going, finished folders count as complete
	// so the figure only climbs.
	const activeAccounts = new Set(pending.map((mailbox) => mailbox.accountId));
	let fetched = 0;
	let total = 0;
	let overran = false;
	for (const mailbox of real) {
		if (!activeAccounts.has(mailbox.accountId)) continue;
		const event = live.get(mailbox.id);
		const estimate =
			event?.estimatedTotal ??
			mailbox.coverageEstimatedTotal ??
			mailbox.providerTotalCount ??
			mailbox.localCount;
		total += estimate;
		if (mailbox.coverage === CoverageStatus.Covered) {
			fetched += estimate;
			continue;
		}
		const done = event?.messagesFetched ?? mailbox.coverageMessagesFetched;
		// Not capped: a provider's own estimate can be far too small (Gmail's list estimate is
		// only a rough guide), and a capped figure would read "complete" while the download runs.
		if (estimate > 0 && done >= estimate) overran = true;
		fetched += done;
	}
	// Once a folder has passed its estimate the fraction means nothing, so say "so far" instead.
	const known = total > 0 && !overran;

	return {
		syncing:
			pending.length > 0
				? { fetched, total: known ? total : null, folders: pending.length }
				: null,
	};
}
