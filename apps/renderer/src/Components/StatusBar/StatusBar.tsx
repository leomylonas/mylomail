import { useEffect, useReducer, useState } from "react";
import { WarningAltFilled } from "@carbon/icons-react";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import type { MailHubConnection } from "@mylomail/renderer/Shell/Backend/HubConnection";
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
				{activity.syncing ? (
					<span>
						Downloading mail
						{activity.syncing.folders > 1
							? ` in ${activity.syncing.folders} folders`
							: ""}
						:{" "}
						{activity.syncing.total
							? `${activity.syncing.fetched} of ${activity.syncing.total}`
							: `${activity.syncing.fetched} so far`}
					</span>
				) : null}
				{pendingContent > 0 ? (
					<span title="Message content is downloaded in the background, newest first, one message at a time. A message you open jumps the queue. Until it arrives a message cannot be read offline or searched by its text.">
						Downloading message content:{" "}
						{(queue.data?.ready ?? 0).toLocaleString()} of{" "}
						{totalContent.toLocaleString()} ({pendingContent.toLocaleString()}{" "}
						queued)
					</span>
				) : null}
				{retrying > 0 ? (
					<span>
						Retrying {retrying} failed{" "}
						{retrying === 1 ? "download" : "downloads"}
					</span>
				) : null}
				{!activity.syncing && pendingContent === 0 && retrying === 0 ? (
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
	let fetched = 0;
	let total = 0;
	for (const mailbox of real) {
		const event = live.get(mailbox.id);
		const estimate =
			event?.estimatedTotal ??
			mailbox.coverageEstimatedTotal ??
			mailbox.providerTotalCount ??
			mailbox.localCount;
		total += estimate;
		fetched +=
			mailbox.coverage === CoverageStatus.Covered
				? estimate
				: Math.min(
						estimate,
						event?.messagesFetched ?? mailbox.coverageMessagesFetched,
					);
	}

	return {
		syncing:
			pending.length > 0
				? { fetched, total: total > 0 ? total : null, folders: pending.length }
				: null,
	};
}
