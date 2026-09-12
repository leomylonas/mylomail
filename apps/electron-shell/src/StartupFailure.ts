export interface StartupFailureDialog {
	showErrorBox(title: string, content: string): void;
}

const maximumDetailLength = 4_000;

/**
 * Surfaces a fatal local-service startup failure before Electron exits. The renderer cannot
 * present this state because no window is opened until the backend is healthy.
 */
export function surfaceStartupFailure(
	dialog: StartupFailureDialog,
	error: unknown,
): void {
	const rawDetail =
		error instanceof Error
			? error.message
			: typeof error === "string"
				? error
				: "The local service exited without an error message.";
	const detail = rawDetail.slice(0, maximumDetailLength);

	dialog.showErrorBox(
		"MyloMail could not start",
		"MyloMail's local service did not start. This can happen when the data directory cannot be opened or its database upgrade fails.\n\n" +
			`Details: ${detail}\n\n` +
			"Close MyloMail and try again. If the problem continues, preserve the data directory when reporting the error.",
	);
}
