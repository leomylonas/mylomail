export {};

declare global {
	interface Window {
		backend?: {
			openAttachment(messageId: string, attachmentId: string): Promise<string>;
		};
		notifications?: {
			show(request: {
				id: string;
				accountId: string;
				title: string;
				body: string;
			}): Promise<void>;
			setNavigationReady(ready: boolean): void;
			onClicked(
				callback: (clicked: {
					notificationId: string;
					accountId: string;
				}) => void,
			): () => void;
		};
		windows?: {
			open(query?: string): Promise<void>;
			reportDraftState(draftId: string | null): Promise<void>;
			focusDraftIfOpen(draftId: string): Promise<boolean>;
			onComposeCloseRequest(callback: () => Promise<boolean>): () => void;
		};
		dialogs?: {
			pickExportFolder(): Promise<string | null>;
		};
		/** The user's own locale, which Chromium's navigator.language may have replaced with a sibling. */
		shellLocale?: string;
		printing?: {
			print(): Promise<boolean>;
		};
		shellSettings?: {
			closeBehaviorChanged(value: number): Promise<void>;
			savePanelLayout(panelLayout: string): Promise<void>;
		};
	}
}
