import { useEffect, useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Button, InlineNotification, ProgressBar } from "@carbon/react";
import type { HubConnection } from "@microsoft/signalr";
import type { ExportJobDto } from "@mylomail/shared-types/SignalR/MyloMail.Api.Contracts";
import { ExportJobStatus } from "@mylomail/shared-types/SignalR/MyloMail.Api.Domain";
import styles from "@mylomail/renderer/Components/ExportAccount/ExportAccount.module.css";

/**
 * Bulk `.eml` export (§13 Export) — the backend job, hub methods and persisted progress have
 * existed since an earlier pass; this is the renderer surface that was missing entirely.
 */
export function ExportAccount({
	hub,
	accountId,
}: {
	hub: HubConnection;
	accountId: string;
}) {
	const queryClient = useQueryClient();
	const queryKey = ["export-status", accountId];

	const status = useQuery({
		queryKey,
		queryFn: () =>
			hub.invoke<ExportJobDto | null>("GetExportStatus", accountId),
	});

	// Progress is broadcast to every connected client, not scoped to the account that started
	// it — refetch and let the accountId check below decide whether this update is ours.
	useEffect(() => {
		const onProgress = () => void status.refetch();
		hub.on("ExportProgress", onProgress);
		return () => hub.off("ExportProgress", onProgress);
	}, [hub, status]);

	const [destination, setDestination] = useState<string | null>(null);

	const start = useMutation({
		mutationFn: async () => {
			const folder = await window.dialogs?.pickExportFolder();
			if (!folder) return;
			setDestination(folder);
			await hub.invoke("StartBulkExport", accountId, folder);
		},
		onSuccess: () => void queryClient.invalidateQueries({ queryKey }),
	});

	const cancel = useMutation({
		mutationFn: async (exportId: string) => {
			await hub.invoke("CancelBulkExport", exportId);
		},
		onSuccess: () => void queryClient.invalidateQueries({ queryKey }),
	});

	const job = status.data;
	const running =
		job?.status === ExportJobStatus.Running ||
		job?.status === ExportJobStatus.CancelRequested;

	return (
		<div className={styles.export}>
			<h4>Export this account</h4>
			<p>
				Writes every message as a <code>.eml</code> file, recreating the folder
				hierarchy on disk.
			</p>

			{!window.dialogs ? (
				<InlineNotification
					kind="info"
					title="Not available in this environment"
					subtitle="Export needs the desktop shell's folder picker."
					lowContrast
					hideCloseButton
				/>
			) : null}

			{job && running ? (
				<div className={styles.progress}>
					<ProgressBar
						label={
							job.status === ExportJobStatus.CancelRequested
								? "Cancelling…"
								: "Exporting…"
						}
						value={job.writtenCount}
						max={Math.max(job.totalCount, 1)}
						helperText={`${job.writtenCount} of ${job.totalCount}`}
					/>
					<Button
						size="sm"
						kind="danger--tertiary"
						disabled={
							job.status === ExportJobStatus.CancelRequested || cancel.isPending
						}
						onClick={() => cancel.mutate(job.id)}
					>
						Cancel
					</Button>
				</div>
			) : (
				<Button
					size="sm"
					disabled={!window.dialogs || start.isPending}
					onClick={() => start.mutate()}
				>
					{start.isPending ? "Choosing folder…" : "Export…"}
				</Button>
			)}

			{job && !running && job.status === ExportJobStatus.Completed ? (
				<InlineNotification
					kind="success"
					title="Export complete"
					subtitle={`${job.writtenCount} messages written${destination ? ` to ${destination}` : ""}.`}
					lowContrast
					hideCloseButton
				/>
			) : null}

			{job && !running && job.status === ExportJobStatus.Failed ? (
				<InlineNotification
					kind="error"
					title="Export failed"
					subtitle={job.lastError ?? "Something went wrong partway through."}
					lowContrast
					hideCloseButton
				/>
			) : null}

			{job && !running && job.status === ExportJobStatus.Cancelled ? (
				<InlineNotification
					kind="info"
					title="Export cancelled"
					subtitle={`${job.writtenCount} of ${job.totalCount} messages were written before cancelling.`}
					lowContrast
					hideCloseButton
				/>
			) : null}

			{start.isError ? (
				<InlineNotification
					kind="error"
					title="Could not start the export"
					subtitle={
						start.error instanceof Error
							? start.error.message
							: String(start.error)
					}
					lowContrast
					hideCloseButton
				/>
			) : null}

			{cancel.isError ? (
				<InlineNotification
					kind="error"
					title="Could not cancel the export"
					subtitle={
						cancel.error instanceof Error
							? cancel.error.message
							: String(cancel.error)
					}
					lowContrast
					hideCloseButton
				/>
			) : null}
		</div>
	);
}
