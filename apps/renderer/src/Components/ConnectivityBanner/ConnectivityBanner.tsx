import { useQuery } from "@tanstack/react-query";
import type { MailHubConnection } from "@mylomail/renderer/Shell/Backend/HubConnection";
import { InlineNotification } from "@carbon/react";
import { queryKeys } from "@mylomail/renderer/Shell/Backend/HubConnection";

/**
 * One calm offline state, replacing the per-mailbox error noise that would otherwise fire on
 * every poll cycle a connection stays down (§7, §15). Renders nothing while online or before
 * the first read of connectivity state completes.
 */
export function ConnectivityBanner({ hub }: { hub: MailHubConnection }) {
	const connectivity = useQuery({
		queryKey: queryKeys.connectivity(),
		queryFn: () => hub.getConnectivity(),
	});

	if (connectivity.data !== false) return null;

	return (
		<InlineNotification
			kind="warning"
			lowContrast
			hideCloseButton
			title="You appear to be offline"
			subtitle="MyloMail will resume automatically once your connection returns."
		/>
	);
}
