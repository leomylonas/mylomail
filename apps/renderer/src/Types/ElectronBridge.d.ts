export {};

declare global {
	interface Window {
		backend?: {
			openAttachment(messageId: string, attachmentId: string): Promise<string>;
		};
	}
}
