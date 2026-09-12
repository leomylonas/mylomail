/**
 * `DataTransfer` MIME types shared between drag sources and drop targets (§13 Epic 2).
 * Centralised so a source and a target can't drift onto different strings.
 */
export const messageDragType = "application/x-mylomail-message-id";
export const mailboxDragType = "application/x-mylomail-mailbox-id";
export const accountDragType = "application/x-mylomail-account-id";

export interface MessageDragPayload {
	accountId: string;
	sourceMailboxId: string;
	messageIds: string[];
}

export function serialiseMessageDrag(payload: MessageDragPayload): string {
	return JSON.stringify(payload);
}

export function parseMessageDrag(value: string): MessageDragPayload | null {
	try {
		const payload = JSON.parse(value) as Partial<MessageDragPayload>;
		return typeof payload.accountId === "string" &&
			typeof payload.sourceMailboxId === "string" &&
			Array.isArray(payload.messageIds) &&
			payload.messageIds.length > 0 &&
			payload.messageIds.every((messageId) => typeof messageId === "string")
			? {
					accountId: payload.accountId,
					sourceMailboxId: payload.sourceMailboxId,
					messageIds: payload.messageIds,
				}
			: null;
	} catch {
		return null;
	}
}
