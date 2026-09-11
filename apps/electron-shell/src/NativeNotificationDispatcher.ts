import type { NotificationRequest } from "@mylomail/electron-shell/BackendConnection";

/**
 * Process-local owner of native notification dispatch.
 *
 * Every renderer has its own SignalR connection, so `Clients.All` hands the same durable
 * notification to every open window. The Electron main process is the one shared owner that can
 * turn those relays into one OS notification. This dispatcher suppresses only that same-process
 * fan-out; the backend's durable `DeliveredAt` record remains authoritative across restarts.
 * Claims expire so a long-running shell does not retain every notification id forever and a later
 * at-least-once redispatch can still retry. A native API failure releases its claim immediately.
 */
export class NativeNotificationDispatcher {
	private readonly recent = new Map<string, number>();

	public constructor(
		private readonly showNative: (request: NotificationRequest) => void,
		private readonly now: () => number = Date.now,
		private readonly retentionMilliseconds = 5 * 60 * 1000,
	) {}

	/**
	 * Shows a notification once in the retention window. A duplicate resolves successfully so
	 * every relaying renderer remains able to confirm the durable record as delivered.
	 */
	public dispatch(request: NotificationRequest): void {
		const now = this.now();
		const cutoff = now - this.retentionMilliseconds;
		for (const [id, claimedAt] of this.recent) {
			if (claimedAt > cutoff) break;
			this.recent.delete(id);
		}

		if (this.recent.has(request.id)) return;
		this.recent.set(request.id, now);
		try {
			this.showNative(request);
		} catch (error) {
			this.recent.delete(request.id);
			throw error;
		}
	}
}
