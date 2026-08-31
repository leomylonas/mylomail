/**
 * Channels for the master-password prompt.
 *
 * Separate from both sides for the same reason as the backend connection contract: the
 * preload imports `contextBridge`, which does not exist in the main process.
 */
export const masterPasswordSubmitChannel = "master-password:submit";
export const masterPasswordCancelChannel = "master-password:cancel";
