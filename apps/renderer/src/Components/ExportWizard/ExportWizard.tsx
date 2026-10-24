import { useEffect, useRef, useState } from "react";
import { Close } from "@carbon/icons-react";
import {
	Button,
	Checkbox,
	IconButton,
	InlineNotification,
	ProgressBar,
	ProgressIndicator,
	ProgressStep,
	RadioButton,
	RadioButtonGroup,
	Select,
	SelectItem,
} from "@carbon/react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import type { MailHubConnection } from "@mylomail/renderer/Shell/Backend/HubConnection";
import type { Account } from "@mylomail/renderer/Types/Account";
import type { ExportJobDto } from "@mylomail/shared-types/SignalR/MyloMail.Api.Contracts";
import {
	ExportFileNaming,
	ExportFormat,
	ExportJobStatus,
} from "@mylomail/shared-types/SignalR/MyloMail.Api.Domain";
import {
	buildExportTree,
	checkState,
	describeSelection,
	formatBytes,
	toggleNode,
	type ExportTreeNode,
} from "@mylomail/renderer/Components/ExportWizard/ExportSelection";
import styles from "@mylomail/renderer/Components/ExportWizard/ExportWizard.module.css";

const recentResultMs = 10 * 60 * 1000;

const stepTitles = ["What to export", "Format", "Where to"] as const;

const isRunning = (job: ExportJobDto) =>
	job.status === ExportJobStatus.Running ||
	job.status === ExportJobStatus.CancelRequested;

/**
 * Exports an account's mail to disk. The export itself is a backend job on its own queue, so
 * closing this dialog ("Minimise") leaves it running; its progress stays in the status bar and
 * the background-activity dialog, and reopening the wizard shows it again.
 */
export function ExportWizard({
	hub,
	accounts,
	onClose,
}: {
	hub: MailHubConnection;
	accounts: readonly Account[];
	onClose: () => void;
}) {
	const queryClient = useQueryClient();
	const dialogRef = useRef<HTMLDialogElement>(null);
	useEffect(() => {
		const dialog = dialogRef.current;
		if (dialog && !dialog.open) {
			dialog.showModal();
			dialog.focus();
		}
	}, []);

	const [step, setStep] = useState(0);
	const [accountId, setAccountId] = useState(accounts[0]?.id ?? "");
	const [chosen, setChosen] = useState<Record<string, ReadonlySet<string>>>({});
	const [format, setFormat] = useState<ExportFormat>(ExportFormat.EmlFolders);
	const [naming, setNaming] = useState<ExportFileNaming>(
		ExportFileNaming.DateAndSubject,
	);
	const [destination, setDestination] = useState<string | null>(null);
	// The export being watched; until one is started, a running one found on opening.
	const [watchedId, setWatchedId] = useState<string | null>(null);
	const [startingNew, setStartingNew] = useState(false);
	const [openedAt] = useState(() => Date.now());

	const exports = useQuery({
		queryKey: ["exports"],
		queryFn: () => hub.getExports(),
		refetchInterval: (query) =>
			query.state.data?.some(isRunning) ? 1_000 : false,
	});
	useEffect(
		() =>
			hub.subscribe(
				"exportProgress",
				() => void queryClient.invalidateQueries({ queryKey: ["exports"] }),
			).dispose,
		[hub, queryClient],
	);

	const folders = useQuery({
		queryKey: ["export-folders", accountId],
		queryFn: () => hub.getExportFolders(accountId),
		enabled: accountId !== "",
	});
	const tree = buildExportTree(folders.data ?? []);
	// Everything, until the user changes it.
	const selected =
		chosen[accountId] ?? new Set((folders.data ?? []).map((f) => f.mailboxId));
	const summary = describeSelection(folders.data ?? [], selected);

	const start = useMutation({
		mutationFn: async () =>
			hub.startBulkExport({
				accountId,
				mailboxIds: [...selected],
				format,
				fileNaming: naming,
				destinationPath: destination!,
			}),
		onSuccess: (id) => {
			setWatchedId(id);
			setStartingNew(false);
			void queryClient.invalidateQueries({ queryKey: ["exports"] });
		},
	});
	const cancel = useMutation({
		mutationFn: (id: string) => hub.cancelBulkExport(id),
		onSuccess: () =>
			void queryClient.invalidateQueries({ queryKey: ["exports"] }),
	});

	const jobs = exports.data ?? [];
	// On opening: the export in progress, or one that ended a few minutes ago, so a result that
	// arrived while the wizard was minimised is still there to be read.
	const watched = watchedId
		? jobs.find((job) => job.id === watchedId)
		: jobs.find(
				(job) =>
					isRunning(job) ||
					(job.completedAt !== undefined &&
						openedAt - new Date(job.completedAt).getTime() < recentResultMs),
			);
	const showProgress = watched !== undefined && !startingNew;

	const pickDestination = async () => {
		const folder = await window.dialogs?.pickExportFolder();
		if (folder) setDestination(folder);
	};

	const canAdvance =
		step === 0 ? summary.folderCount > 0 : step === 1 ? true : false;

	return (
		<dialog
			ref={dialogRef}
			className={styles.dialog}
			aria-labelledby="export-wizard-title"
			tabIndex={-1}
			onClose={onClose}
		>
			<header className={styles.header}>
				<h2 id="export-wizard-title">Export mail</h2>
				<IconButton
					label={showProgress ? "Minimise" : "Close"}
					kind="ghost"
					size="md"
					align="bottom-end"
					onClick={onClose}
				>
					<Close size={20} />
				</IconButton>
			</header>

			{showProgress ? (
				<>
					<ExportProgress
						job={watched}
						accountName={
							accounts.find((a) => a.id === watched.accountId)?.displayName ??
							""
						}
					/>
					<footer className={styles.footer}>
						{isRunning(watched) ? (
							<>
								<Button
									kind="danger--tertiary"
									size="md"
									disabled={
										watched.status === ExportJobStatus.CancelRequested ||
										cancel.isPending
									}
									onClick={() => cancel.mutate(watched.id)}
								>
									Cancel export
								</Button>
								<Button kind="primary" size="md" onClick={onClose}>
									Minimise
								</Button>
							</>
						) : (
							<>
								<Button
									kind="secondary"
									size="md"
									onClick={() => {
										setWatchedId(null);
										setStartingNew(true);
										setStep(0);
									}}
								>
									New export
								</Button>
								<Button kind="primary" size="md" onClick={onClose}>
									Close
								</Button>
							</>
						)}
					</footer>
				</>
			) : (
				<>
					<div className={styles.steps}>
						<ProgressIndicator currentIndex={step} spaceEqually>
							{stepTitles.map((title) => (
								<ProgressStep key={title} label={title} />
							))}
						</ProgressIndicator>
					</div>
					<div className={styles.body}>
						{step === 0 ? (
							<>
								<Select
									id="export-account"
									labelText="Account"
									value={accountId}
									onChange={(event) => setAccountId(event.target.value)}
								>
									{accounts.map((account) => (
										<SelectItem
											key={account.id}
											value={account.id}
											text={account.displayName}
										/>
									))}
								</Select>
								<div className={styles.treeActions}>
									<Button
										kind="ghost"
										size="sm"
										onClick={() =>
											setChosen({
												...chosen,
												[accountId]: new Set(
													(folders.data ?? []).map((f) => f.mailboxId),
												),
											})
										}
									>
										Select all
									</Button>
									<Button
										kind="ghost"
										size="sm"
										onClick={() =>
											setChosen({ ...chosen, [accountId]: new Set() })
										}
									>
										Select none
									</Button>
								</div>
								<div
									className={styles.tree}
									role="group"
									aria-label="Folders to export"
								>
									{folders.isPending ? <p>Loading folders…</p> : null}
									<FolderTree
										nodes={tree}
										selected={selected}
										onToggle={(node) =>
											setChosen({
												...chosen,
												[accountId]: toggleNode(node, selected),
											})
										}
									/>
								</div>
								<p className={styles.hint}>
									{summary.folderCount === 0
										? "Choose at least one folder."
										: `${summary.messageCount.toLocaleString()} messages in ${summary.folderCount} ${summary.folderCount === 1 ? "folder" : "folders"}, about ${formatBytes(summary.sizeBytes)} of mail.`}
								</p>
								{summary.notDownloaded > 0 ? (
									<InlineNotification
										kind="info"
										lowContrast
										hideCloseButton
										title="Some messages are not downloaded yet"
										subtitle={`${summary.notDownloaded.toLocaleString()} will be fetched from the server as the export goes, so it needs a connection and may be slower.`}
									/>
								) : null}
								{summary.incompleteFolders.length > 0 ? (
									<InlineNotification
										kind="warning"
										lowContrast
										hideCloseButton
										title="Older mail is not on this computer"
										subtitle={`Only what has been downloaded is exported. Still downloading: ${summary.incompleteFolders.join(", ")}.`}
									/>
								) : null}
							</>
						) : null}

						{step === 1 ? (
							<>
								<RadioButtonGroup
									legendText="Format"
									name="export-format"
									orientation="vertical"
									valueSelected={format}
									onChange={(value) => setFormat(value as ExportFormat)}
								>
									<RadioButton
										id="export-eml"
										labelText="Folder of .eml files, matching the folder structure"
										value={ExportFormat.EmlFolders}
									/>
									<RadioButton
										id="export-mbox"
										labelText=".mbox files, one per folder, with subfolders in a .sbd folder beside it"
										value={ExportFormat.Mbox}
									/>
									<RadioButton
										id="export-pst"
										labelText="Outlook PST file, one for the whole selection"
										value={ExportFormat.Pst}
									/>
								</RadioButtonGroup>
								{format === ExportFormat.EmlFolders ? (
									<RadioButtonGroup
										legendText="Name each .eml file by"
										name="export-naming"
										orientation="vertical"
										valueSelected={naming}
										onChange={(value) => setNaming(value as ExportFileNaming)}
									>
										<RadioButton
											id="export-name-date"
											labelText="Date and subject (2026-10-21 1542 - Welcome.eml)"
											value={ExportFileNaming.DateAndSubject}
										/>
										<RadioButton
											id="export-name-id"
											labelText="Message id"
											value={ExportFileNaming.MessageId}
										/>
									</RadioButtonGroup>
								) : null}
							</>
						) : null}

						{step === 2 ? (
							<>
								<h3>Destination</h3>
								{!window.dialogs ? (
									<InlineNotification
										kind="info"
										lowContrast
										hideCloseButton
										title="Not available in this environment"
										subtitle="Export needs the desktop shell's folder picker."
									/>
								) : null}
								<div className={styles.destination}>
									<Button
										kind="tertiary"
										size="md"
										disabled={!window.dialogs}
										onClick={() => void pickDestination()}
									>
										{destination ? "Change folder…" : "Choose folder…"}
									</Button>
									<span className={styles.path} title={destination ?? ""}>
										{destination ?? "No folder chosen"}
									</span>
								</div>
								<dl className={styles.summary}>
									<dt>Account</dt>
									<dd>
										{accounts.find((a) => a.id === accountId)?.displayName}
									</dd>
									<dt>Folders</dt>
									<dd>
										{summary.folderCount} (
										{summary.messageCount.toLocaleString()} messages, about{" "}
										{formatBytes(summary.sizeBytes)})
									</dd>
									<dt>Format</dt>
									<dd>{describeFormat(format, naming)}</dd>
								</dl>
								<p className={styles.hint}>
									The export runs in the background. Mail that arrives in these
									folders while it runs is included.
								</p>
								{start.isError ? (
									<InlineNotification
										kind="error"
										lowContrast
										hideCloseButton
										title="Could not start the export"
										subtitle={
											start.error instanceof Error
												? start.error.message
												: String(start.error)
										}
									/>
								) : null}
							</>
						) : null}
					</div>
					<footer className={styles.footer}>
						<Button kind="ghost" size="md" onClick={onClose}>
							Cancel
						</Button>
						<Button
							kind="secondary"
							size="md"
							disabled={step === 0}
							onClick={() => setStep(step - 1)}
						>
							Back
						</Button>
						{step < 2 ? (
							<Button
								kind="primary"
								size="md"
								disabled={!canAdvance}
								onClick={() => setStep(step + 1)}
							>
								Next
							</Button>
						) : (
							<Button
								kind="primary"
								size="md"
								disabled={!destination || start.isPending}
								onClick={() => start.mutate()}
							>
								Start export
							</Button>
						)}
					</footer>
				</>
			)}
		</dialog>
	);
}

function describeFormat(format: ExportFormat, naming: ExportFileNaming) {
	if (format === ExportFormat.Mbox) return "mbox files";
	if (format === ExportFormat.Pst) return "Outlook PST file";
	return `.eml files named by ${naming === ExportFileNaming.DateAndSubject ? "date and subject" : "message id"}`;
}

function FolderTree({
	nodes,
	selected,
	onToggle,
}: {
	nodes: readonly ExportTreeNode[];
	selected: ReadonlySet<string>;
	onToggle: (node: ExportTreeNode) => void;
}) {
	if (nodes.length === 0) return null;
	return (
		<ul>
			{nodes.map((node) => {
				const state = checkState(node, selected);
				return (
					<li key={node.folder.mailboxId}>
						<Checkbox
							id={`export-folder-${node.folder.mailboxId}`}
							labelText={
								<>
									{node.folder.name}{" "}
									<span className={styles.count}>
										({node.folder.messageCount.toLocaleString()})
									</span>
								</>
							}
							checked={state === "checked"}
							indeterminate={state === "partial"}
							onChange={() => onToggle(node)}
						/>
						<FolderTree
							nodes={node.children}
							selected={selected}
							onToggle={onToggle}
						/>
					</li>
				);
			})}
		</ul>
	);
}

function ExportProgress({
	job,
	accountName,
}: {
	job: ExportJobDto;
	accountName: string;
}) {
	const running = isRunning(job);
	const label =
		job.status === ExportJobStatus.CancelRequested
			? "Cancelling…"
			: running
				? `Exporting ${accountName}`
				: job.status === ExportJobStatus.Completed
					? "Export complete"
					: job.status === ExportJobStatus.Cancelled
						? "Export cancelled"
						: "Export failed";
	return (
		<div className={styles.body}>
			<ProgressBar
				label={label}
				value={job.writtenCount + job.skippedCount}
				max={Math.max(job.totalCount, 1)}
				status={
					running
						? "active"
						: job.status === ExportJobStatus.Completed
							? "finished"
							: "error"
				}
				helperText={`${job.writtenCount.toLocaleString()} of ${job.totalCount.toLocaleString()} messages written`}
			/>
			<dl className={styles.summary}>
				<dt>To</dt>
				<dd>{job.destinationPath}</dd>
				<dt>Format</dt>
				<dd>
					{job.format === ExportFormat.Mbox
						? "mbox"
						: job.format === ExportFormat.Pst
							? "Outlook PST"
							: ".eml files"}
				</dd>
			</dl>
			{job.skippedCount > 0 ? (
				<InlineNotification
					kind="warning"
					lowContrast
					hideCloseButton
					title={`${job.skippedCount} messages were not written`}
					subtitle={
						job.lastError ??
						"They were moved or deleted after the export began."
					}
				/>
			) : null}
			{running ? (
				<p className={styles.hint}>
					You can minimise this: the export carries on in the background, and
					its progress stays in the status bar. Mail arriving in the exported
					folders meanwhile is included.
				</p>
			) : null}
			{job.status === ExportJobStatus.Completed ? (
				<p className={styles.hint}>
					Mail that arrived after the export finished is not in it.
				</p>
			) : null}
			{job.status === ExportJobStatus.Failed ? (
				<InlineNotification
					kind="error"
					lowContrast
					hideCloseButton
					title="The export stopped"
					subtitle={job.lastError ?? "Something went wrong partway through."}
				/>
			) : null}
		</div>
	);
}
