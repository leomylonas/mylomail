import { contextBridge, ipcRenderer } from "electron";
import {
	masterPasswordCancelChannel,
	masterPasswordSubmitChannel,
} from "@mylomail/electron-shell/MasterPassword/MasterPasswordChannels";

/**
 * The prompt's only capability: hand one password back, or decline.
 *
 * Deliberately write-only. The prompt can send a password to the main process and can learn
 * nothing — not whether it was right, not what else the app knows.
 */
contextBridge.exposeInMainWorld("masterPassword", {
	submit: (password: string) =>
		ipcRenderer.send(masterPasswordSubmitChannel, password),
	cancel: () => ipcRenderer.send(masterPasswordCancelChannel),
});
