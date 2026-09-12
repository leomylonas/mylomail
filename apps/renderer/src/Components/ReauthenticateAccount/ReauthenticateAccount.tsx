import { useState } from "react";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import {
	ActionableNotification,
	InlineNotification,
	Modal,
	PasswordInput,
} from "@carbon/react";
import type { HubConnection } from "@microsoft/signalr";
import {
	fetchApi,
	MutationTransportError,
	notificationForError,
} from "@mylomail/renderer/Shell/Backend/ProblemDetailsTransport";
import { useWindowNotifications } from "@mylomail/renderer/Shell/Registries/Notifications/UseNotifications";
import { notify } from "@mylomail/renderer/Shell/Registries/Notifications/NotificationStore";

/**
 * Re-verifies an account stuck in `NeedsReauth`/`Error` (§3). A password field that is fine
 * left blank: a certificate-only rejection (the server's certificate rotated, since pinned via
 * `TrustCertificate`) needs nothing resupplied — the stored credential was never wrong.
 */
export function ReauthenticateAccount({
	hub,
	accountId,
	onReauthenticated,
	onClose,
}: {
	hub: HubConnection;
	accountId: string;
	onReauthenticated: () => void;
	onClose: () => void;
}) {
	const [secret, setSecret] = useState("");
	const queryClient = useQueryClient();
	const { store: notifications } = useWindowNotifications();

	const attempt = useMutation({
		mutationFn: async () => {
			await fetchApi(`/accounts/${accountId}/reauthenticate`, {
				method: "POST",
				headers: { "Content-Type": "application/json" },
				body: JSON.stringify({ secret: secret || null }),
			});
		},
		onSuccess: () => {
			setSecret("");
			void queryClient.invalidateQueries({ queryKey: ["accounts"] });
			onReauthenticated();
		},
	});

	// Pinning happens over the hub (the account already exists, unlike AddAccount's dilemma),
	// then the same REST attempt runs again — now against a fingerprint the server will match.
	const trustAndRetry = useMutation({
		mutationFn: async (certificate: {
			hostname: string;
			sha256Fingerprint: string;
		}) => {
			await hub.invoke(
				"TrustCertificate",
				accountId,
				certificate.hostname,
				certificate.sha256Fingerprint,
			);
			await attempt.mutateAsync();
		},
		// A rejection of attempt.mutateAsync() already surfaces through attempt's own
		// isError/error state below — this only catches TrustCertificate itself failing,
		// which nothing else reads.
		onError: (error: unknown) => {
			if (attempt.error === error) return;
			notify(
				notifications,
				notificationForError(error, "The certificate could not be trusted"),
			);
		},
	});

	const error =
		attempt.isError && attempt.error instanceof MutationTransportError
			? attempt.error
			: null;

	return (
		<Modal
			open
			modalHeading="Reauthenticate account"
			primaryButtonText="Reauthenticate"
			secondaryButtonText="Cancel"
			primaryButtonDisabled={attempt.isPending || trustAndRetry.isPending}
			onRequestSubmit={() => attempt.mutate()}
			onRequestClose={onClose}
			onSecondarySubmit={onClose}
		>
			<p>This account needs attention before it can sync again.</p>
			<PasswordInput
				id="reauthenticate-secret"
				labelText="Password"
				helperText="Leave blank to just retry — enough if only the server's certificate changed."
				value={secret}
				onChange={(event) => setSecret(event.target.value)}
			/>
			{error ? (
				error.presentation.action === "trust-certificate" &&
				error.presentation.certificate ? (
					<ActionableNotification
						kind="warning"
						title={error.presentation.title}
						subtitle={`${error.presentation.detail} Only continue if you recognise and trust ${error.presentation.certificate.hostname}.`}
						actionButtonLabel="Trust this certificate and retry"
						onActionButtonClick={() =>
							trustAndRetry.mutate(error.presentation.certificate!)
						}
						lowContrast
						hideCloseButton
						inline
					/>
				) : (
					<InlineNotification
						kind="error"
						title={error.presentation.title}
						subtitle={error.presentation.detail}
						lowContrast
						hideCloseButton
					/>
				)
			) : null}
		</Modal>
	);
}
