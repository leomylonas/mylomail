import { useEffect, useState } from "react";
import { useQueryClient } from "@tanstack/react-query";
import type { HubConnection } from "@microsoft/signalr";
import { connectHub } from "@mylomail/renderer/Shell/Backend/HubConnection";
import { useWindowNotifications } from "@mylomail/renderer/Shell/Registries/Notifications/UseNotifications";

export type HubStatus = "connecting" | "connected" | "failed";

/** Opens this window's hub connection for as long as the window lives. */
export function useHub(): { hub: HubConnection | null; status: HubStatus } {
	const queryClient = useQueryClient();
	const { store: notifications } = useWindowNotifications();
	const [hub, setHub] = useState<HubConnection | null>(null);
	const [status, setStatus] = useState<HubStatus>("connecting");

	useEffect(() => {
		const connection = connectHub(queryClient, notifications);
		let cancelled = false;

		connection
			.start()
			.then(() => {
				if (cancelled) return;
				setHub(connection);
				setStatus("connected");
			})
			.catch((error: unknown) => {
				console.error(`hub connection failed: ${String(error)}`);
				if (!cancelled) setStatus("failed");
			});

		return () => {
			cancelled = true;
			void connection.stop();
		};
	}, [queryClient, notifications]);

	return { hub, status };
}
