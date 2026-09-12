interface PrintableWebContents {
	print(
		options: { printBackground: boolean },
		callback: (success: boolean, failureReason: string) => void,
	): void;
}

/**
 * Prints the sender's current document through Electron rather than the renderer's browser
 * API. The main process owns print options, treats the native dialog's ordinary cancel as a
 * non-error result, and rejects only failures that need feedback.
 */
export function printWebContents(
	webContents: PrintableWebContents,
): Promise<boolean> {
	return new Promise((resolve, reject) => {
		webContents.print({ printBackground: true }, (success, failureReason) => {
			if (success) {
				resolve(true);
			} else if (failureReason === "Print job canceled") {
				resolve(false);
			} else {
				reject(new Error(failureReason || "Printing failed."));
			}
		});
	});
}
