import { useState } from "react";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import {
	ActionableNotification,
	InlineNotification,
	Modal,
	PasswordInput,
} from "@carbon/react";
import type { HubConnection } from "@microsoft/signalr";
import { ErrorCategory } from "@mylomail/shared-types/SignalR/MyloMail.Api.Errors";
import {
	present,
	type ErrorPresentation,
} from "@mylomail/renderer/Shell/Registries/Errors/ErrorPresentation";

/** The subset of RFC 7807 this endpoint's failures actually carry (§15). */
interface ProblemResponse {
	title?: string;
	detail?: string;
	category?: ErrorCategory;
	extensions?: Record<string, unknown>;
}

class ReauthenticateError extends Error {
	constructor(public presentation: ErrorPresentation) {
		super(presentation.detail);
	}
}

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

	const attempt = useMutation({
		mutationFn: async () => {
			const response = await fetch(`/accounts/${accountId}/reauthenticate`, {
				method: "POST",
				headers: { "Content-Type": "application/json" },
				body: JSON.stringify({ secret: secret || null }),
			});
			if (!response.ok) {
				const problem = (await response
					.json()
					.catch(() => null)) as ProblemResponse | null;
				const presentation =
					problem?.category !== undefined
						? present(problem.category, problem.detail, problem.extensions)
						: {
								title: problem?.title ?? "Could not reauthenticate",
								detail:
									problem?.detail ??
									`Reauthenticating failed (${response.status}).`,
								transient: false as const,
							};
				throw new ReauthenticateError(presentation);
			}
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
	});

	const error =
		attempt.isError && attempt.error instanceof ReauthenticateError
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
