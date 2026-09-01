export {};

declare global {
	interface Window {
		backend?: {
			openAttachment(messageId: string, attachmentId: string): Promise<string>;
		};
		notifications?: {
			show(request: {
				id: string;
				title: string;
				body: string;
				messageId: string;
			}): Promise<void>;
			onClicked(callback: (messageId: string) => void): () => void;
		};
	}
}
