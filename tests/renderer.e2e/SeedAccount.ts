import type { Page } from "@playwright/test";
import {
	CertificateTrustMode,
	ImapAuthMethod,
	MailTransportSecurity,
	InitialSyncMode,
	ProviderType,
	SmtpAuthMethod,
} from "@mylomail/shared-types/SignalR/MyloMail.Api.Domain";
import type { AddAccountRequest } from "@mylomail/shared-types/SignalR/MyloMail.Api.Contracts";

/** Mailpit, shared by every tier of the matrix (`pnpm imap:up`). */
const smtpPort = 11025;

/**
 * Adds a matrix IMAP account through the same endpoint the settings UI posts to.
 *
 * One helper rather than a copy of the payload per spec: the request shape is versioned with
 * the backend — the transport-security and authentication modes replaced a single `useSsl`
 * switch — and six hand-maintained copies drifted out of date silently, each failing as a
 * bare `400` with nothing to say which field was wrong.
 *
 * The matrix refuses plaintext password authentication, so STARTTLS is mandatory here, and
 * its certificate is locally generated and self-signed — hence `TrustAll`, which is what the
 * interactive form's "Accept all certificates and retry" path sets too.
 */
export async function createImapAccount(
	window: Page,
	imapPort: number,
): Promise<number> {
	const request: AddAccountRequest = {
		displayName: "Matrix",
		providerType: ProviderType.Imap,
		emailAddress: "test@mylomail.local",
		secret: "password",
		certificateTrustMode: CertificateTrustMode.TrustAll,
		imap: {
			host: "127.0.0.1",
			port: imapPort,
			imapSecurity: MailTransportSecurity.StartTls,
			authMethod: ImapAuthMethod.Password,
			userName: "test@mylomail.local",
			smtpHost: "127.0.0.1",
			smtpPort,
			// Mailpit is a plaintext sink with no certificate, so STARTTLS is not on offer
			// here. Unauthenticated is what it accepts and what the app allows on a
			// plaintext transport — a password over one is refused outright (§2).
			smtpSecurity: MailTransportSecurity.None,
			smtpAuthMethod: SmtpAuthMethod.None,
			reuseImapCredentialForSmtp: false,
		},
		initialSyncMode: InitialSyncMode.Full,
	};

	return await window.evaluate(async (body) => {
		const response = await fetch("/accounts", {
			method: "POST",
			headers: { "Content-Type": "application/json" },
			body: JSON.stringify(body),
		});
		if (!response.ok) {
			// The status alone is not enough to act on: a rejected field and a rejected
			// credential are both 400 from the caller's point of view.
			throw new Error(
				`POST /accounts returned ${response.status}: ${await response.text()}`,
			);
		}
		return response.status;
	}, request);
}
