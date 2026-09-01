/**
 * `DataTransfer` MIME types shared between drag sources and drop targets (§13 Epic 2).
 * Centralised so a source and a target can't drift onto different strings.
 */
export const messageDragType = "application/x-mylomail-message-id";
export const mailboxDragType = "application/x-mylomail-mailbox-id";
