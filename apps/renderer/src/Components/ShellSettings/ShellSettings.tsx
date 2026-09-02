import { useQuery, useQueryClient } from "@tanstack/react-query";
import {
	Button,
	InlineNotification,
	RadioButton,
	RadioButtonGroup,
	StructuredListBody,
	StructuredListCell,
	StructuredListRow,
	StructuredListWrapper,
} from "@carbon/react";
import { useWindowNotifications } from "@mylomail/renderer/Shell/Registries/Notifications/UseNotifications";
import { notify } from "@mylomail/renderer/Shell/Registries/Notifications/NotificationStore";
import styles from "@mylomail/renderer/Components/ShellSettings/ShellSettings.module.css";

/** Mirrors server/MyloMail.Api/Domain/AppSettings.cs's CloseBehavior/ThemePreference enum
 * ordinals — read over REST, not the typed SignalR hub, so no JsonStringEnumConverter
 * applies and these arrive as numbers. */
const closeBehavior = { QuitApp: 0, MinimizeToTray: 1 } as const;
const theme = { System: 0, Light: 1, Dark: 2 } as const;

interface ShellSettings {
	closeBehavior: number;
	theme: number;
}

interface TrustedSender {
	address: string;
}

/**
 * The app-wide settings §13 Epic 8 calls for that are not per-account: theme, close
 * behaviour, credential-storage visibility, and the persisted remote-content allow list
 * (§13 Epic 5). Per-account settings (polling, notifications, undo window) live in
 * `AccountSettings` instead — those are a fact about one account, not the installation.
 */
export function ShellSettings({ onClose }: { onClose: () => void }) {
	const queryClient = useQueryClient();
	const { store: notifications } = useWindowNotifications();

	const reportFailure = (title: string) => (error: unknown) =>
		notify(notifications, {
			kind: "error",
			title,
			detail: error instanceof Error ? error.message : String(error),
		});

	const settings = useQuery({
		queryKey: ["shell-settings", "app-settings"],
		queryFn: async (): Promise<ShellSettings> => {
			const response = await fetch("/shell-settings");
			if (!response.ok) {
				return { closeBehavior: closeBehavior.QuitApp, theme: theme.System };
			}
			return (await response.json()) as ShellSettings;
		},
	});

	const credentialStore = useQuery({
		queryKey: ["credential-store-status"],
		queryFn: async (): Promise<{ usingNativeStore: boolean }> => {
			const response = await fetch("/credential-store/status");
			// Not defaulted to "native store, all fine" on failure: this section exists
			// specifically to warn about the fallback store, so a query failure must not
			// assert the one claim it would otherwise exist to contradict.
			if (!response.ok)
				throw new Error(`credential-store/status responded ${response.status}`);
			return (await response.json()) as { usingNativeStore: boolean };
		},
	});

	const trustedSenders = useQuery({
		queryKey: ["remote-content-trusted-senders"],
		queryFn: async (): Promise<TrustedSender[]> => {
			const response = await fetch("/remote-content/trusted-senders");
			if (!response.ok) return [];
			return (await response.json()) as TrustedSender[];
		},
	});

	// Every shell-settings-derived query gets invalidated together: the three settings above
	// all read the one GET /shell-settings row (or a sibling endpoint), so one write
	// invalidates the lot rather than each caller tracking which fields it touched.
	const invalidateShellSettings = () =>
		void queryClient.invalidateQueries({ queryKey: ["shell-settings"] });

	const setCloseBehavior = async (value: number) => {
		try {
			const response = await fetch("/shell-settings/close-behavior", {
				method: "PUT",
				headers: { "Content-Type": "application/json" },
				body: JSON.stringify({ closeBehavior: value }),
			});
			if (!response.ok)
				throw new Error(`close-behavior responded ${response.status}`);
			invalidateShellSettings();
		} catch (error) {
			reportFailure("The close behaviour could not be saved")(error);
		}
	};

	const setTheme = async (value: number) => {
		try {
			const response = await fetch("/shell-settings/theme", {
				method: "PUT",
				headers: { "Content-Type": "application/json" },
				body: JSON.stringify({ theme: value }),
			});
			if (!response.ok) throw new Error(`theme responded ${response.status}`);
			invalidateShellSettings();
		} catch (error) {
			reportFailure("The theme could not be saved")(error);
		}
	};

	const untrustSender = async (address: string) => {
		try {
			const response = await fetch(
				`/remote-content/trusted-senders/${encodeURIComponent(address)}`,
				{
					method: "DELETE",
				},
			);
			if (!response.ok)
				throw new Error(`trusted-senders responded ${response.status}`);
			void queryClient.invalidateQueries({
				queryKey: ["remote-content-trusted-senders"],
			});
		} catch (error) {
			reportFailure("The sender could not be removed")(error);
		}
	};

	return (
		<div className={styles.settings}>
			<h3>Settings</h3>

			<section>
				<h4>Appearance</h4>
				<RadioButtonGroup
					legendText="Theme"
					name="theme"
					valueSelected={String(settings.data?.theme ?? theme.System)}
					onChange={(value) => void setTheme(Number(value))}
				>
					<RadioButton
						labelText="Match system"
						value={String(theme.System)}
						id="theme-system"
					/>
					<RadioButton
						labelText="Light"
						value={String(theme.Light)}
						id="theme-light"
					/>
					<RadioButton
						labelText="Dark"
						value={String(theme.Dark)}
						id="theme-dark"
					/>
				</RadioButtonGroup>
			</section>

			<section>
				<h4>Closing the app</h4>
				<RadioButtonGroup
					legendText="When the last window closes"
					name="close-behavior"
					valueSelected={String(
						settings.data?.closeBehavior ?? closeBehavior.QuitApp,
					)}
					onChange={(value) => void setCloseBehavior(Number(value))}
				>
					<RadioButton
						labelText="Quit MyloMail"
						value={String(closeBehavior.QuitApp)}
						id="close-quit"
					/>
					<RadioButton
						labelText="Keep running in the system tray"
						value={String(closeBehavior.MinimizeToTray)}
						id="close-tray"
					/>
				</RadioButtonGroup>
			</section>

			<section>
				<h4>Credential storage</h4>
				{credentialStore.isError ? (
					<InlineNotification
						kind="error"
						title="Could not determine credential storage"
						subtitle="This section could not be checked. Reopen settings to try again."
						lowContrast
						hideCloseButton
					/>
				) : credentialStore.data && !credentialStore.data.usingNativeStore ? (
					<InlineNotification
						kind="warning"
						title="Using the fallback credential store"
						subtitle="This OS has no native credential store MyloMail could use, so account
							credentials are protected by a master password and stored, encrypted, in the
							local database instead."
						lowContrast
						hideCloseButton
					/>
				) : (
					<InlineNotification
						kind="info"
						title="Using the OS native credential store"
						subtitle="Account credentials are stored in this operating system's own credential
							manager, not in MyloMail's database."
						lowContrast
						hideCloseButton
					/>
				)}
			</section>

			<section>
				<h4>Remote content</h4>
				<p>
					Senders always allowed to load remote content (images, tracking
					pixels) without asking each time.
				</p>
				{trustedSenders.data?.length ? (
					<StructuredListWrapper>
						<StructuredListBody>
							{trustedSenders.data.map((sender) => (
								<StructuredListRow key={sender.address}>
									<StructuredListCell>{sender.address}</StructuredListCell>
									<StructuredListCell>
										<Button
											size="sm"
											kind="ghost"
											onClick={() => void untrustSender(sender.address)}
										>
											Remove
										</Button>
									</StructuredListCell>
								</StructuredListRow>
							))}
						</StructuredListBody>
					</StructuredListWrapper>
				) : (
					<p className={styles.empty}>
						No senders yet — allow one from the &ldquo;Load content&rdquo;
						prompt on a message.
					</p>
				)}
			</section>

			<div>
				<Button size="sm" kind="ghost" onClick={onClose}>
					Close
				</Button>
			</div>
		</div>
	);
}
