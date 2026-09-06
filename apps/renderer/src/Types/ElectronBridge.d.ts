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
				messageId: string | null;
			}): Promise<void>;
			onClicked(
				callback: (clicked: {
					notificationId: string;
					messageId: string | null;
				}) => void,
			): () => void;
		};
		windows?: {
			open(query?: string): Promise<void>;
			reportDraftState(draftId: string | null): Promise<void>;
			focusDraftIfOpen(draftId: string): Promise<boolean>;
		};
		dialogs?: {
			pickExportFolder(): Promise<string | null>;
		};
		shellSettings?: {
			closeBehaviorChanged(value: number): Promise<void>;
		};
	}
}
