import type {
	AuthState,
	CertificateTrustMode,
	InitialSyncMode,
	ProviderType,
} from "@mylomail/shared-types/SignalR/MyloMail.Api.Domain";

/** One account as the `/accounts` endpoint returns it. */
export interface Account {
	id: string;
	displayName: string;
	emailAddress: string;
	color: string;
	pollIntervalSeconds?: number;
	pollingEnabled?: boolean;
	undoSendDelaySeconds?: number;
	notificationsEnabled?: boolean;
	initialSyncMode?: InitialSyncMode;
	initialSyncBoundValue?: number | null;
	authState?: AuthState;
	lastAuthError?: string | null;
	sidebarCollapsed?: boolean;
	attachmentSizeLimitOverride?: number | null;
	maxMessageDownloadMegabytes?: number;
	certificateTrustMode?: CertificateTrustMode;
	providerType?: ProviderType;
	appendToSentOnSend?: boolean | null;
	isThrottled?: boolean;
}
