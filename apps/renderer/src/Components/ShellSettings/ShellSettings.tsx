import { useState } from "react";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import {
	Button,
	InlineNotification,
	RadioButton,
	RadioButtonGroup,
	Select,
	SelectItem,
	StructuredListBody,
	StructuredListCell,
	StructuredListRow,
	StructuredListWrapper,
	TextInput,
} from "@carbon/react";
import type { RemoteContentRuleDto } from "@mylomail/shared-types/Api/Contracts/RemoteContentRuleDto";
import { RemoteContentRuleDecision } from "@mylomail/shared-types/Api/Domain/RemoteContentRuleDecision";
import { RemoteContentRuleScope } from "@mylomail/shared-types/Api/Domain/RemoteContentRuleScope";
import styles from "@mylomail/renderer/Components/ShellSettings/ShellSettings.module.css";
import { notify } from "@mylomail/renderer/Shell/Registries/Notifications/NotificationStore";
import { useWindowNotifications } from "@mylomail/renderer/Shell/Registries/Notifications/UseNotifications";

/** Mirrors server/MyloMail.Api/Domain/AppSettings.cs's CloseBehavior/ThemePreference enum
 * ordinals — read over REST, not the typed SignalR hub, so no JsonStringEnumConverter
 * applies and these arrive as numbers. */
const closeBehavior = { QuitApp: 0, MinimizeToTray: 1 } as const;
const theme = { System: 0, Light: 1, Dark: 2 } as const;

interface ShellSettings {
	closeBehavior: number;
	theme: number;
}

/**
 * The app-wide settings §13 Epic 8 calls for that are not per-account: theme, close
 * behaviour, credential-storage visibility, and persisted sender/domain remote-content
 * allow/block rules (§13 Epic 5). Per-account settings live in `AccountSettings`.
 */
export function ShellSettings({ onClose }: { onClose: () => void }) {
	const queryClient = useQueryClient();
	const { store: notifications } = useWindowNotifications();
	const [ruleScope, setRuleScope] = useState(RemoteContentRuleScope.Sender);
	const [ruleDecision, setRuleDecision] = useState(
		RemoteContentRuleDecision.Allow,
	);
	const [ruleValue, setRuleValue] = useState("");

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

	const remoteContentRules = useQuery({
		queryKey: ["remote-content-rules"],
		queryFn: async (): Promise<RemoteContentRuleDto[]> => {
			const response = await fetch("/remote-content/rules");
			if (!response.ok) return [];
			return (await response.json()) as RemoteContentRuleDto[];
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
			// The shell reads CloseBehavior once at startup (§13 Epic 10) — without telling it
			// directly, this save takes effect only after the app is next relaunched, with no
			// error to explain why closing the window didn't behave as just chosen. This runs
			// after the save is already known to have succeeded and outside its try block: the
			// PUT is the durable write, so a failure here must not be reported as the save
			// itself having failed.
			try {
				await window.shellSettings?.closeBehaviorChanged(value);
			} catch {
				// Best-effort only. The setting is saved; the running app just won't act on it
				// until relaunched, which is the same outcome as before this push existed.
			}
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

	const deleteRule = async (id: string) => {
		try {
			const response = await fetch(`/remote-content/rules/${id}`, {
				method: "DELETE",
			});
			if (!response.ok)
				throw new Error(`remote-content/rules responded ${response.status}`);
			void queryClient.invalidateQueries({
				queryKey: ["remote-content-rules"],
			});
		} catch (error) {
			reportFailure("The remote-content rule could not be removed")(error);
		}
	};

	const putRule = async () => {
		try {
			const response = await fetch("/remote-content/rules", {
				method: "PUT",
				headers: { "Content-Type": "application/json" },
				body: JSON.stringify({
					scope: ruleScope,
					decision: ruleDecision,
					value: ruleValue,
				}),
			});
			if (!response.ok)
				throw new Error(`remote-content/rules responded ${response.status}`);
			setRuleValue("");
			void queryClient.invalidateQueries({
				queryKey: ["remote-content-rules"],
			});
		} catch (error) {
			reportFailure("The remote-content rule could not be saved")(error);
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
					Exact sender rules override domain rules. Without a matching allow
					rule, remote images and tracking pixels stay blocked.
				</p>
				<form
					onSubmit={(event) => {
						event.preventDefault();
						void putRule();
					}}
				>
					<Select
						id="remote-content-rule-decision"
						labelText="Decision"
						value={ruleDecision}
						onChange={(event) => setRuleDecision(Number(event.target.value))}
					>
						<SelectItem value={RemoteContentRuleDecision.Allow} text="Allow" />
						<SelectItem value={RemoteContentRuleDecision.Block} text="Block" />
					</Select>
					<Select
						id="remote-content-rule-scope"
						labelText="Applies to"
						value={ruleScope}
						onChange={(event) => setRuleScope(Number(event.target.value))}
					>
						<SelectItem value={RemoteContentRuleScope.Sender} text="Sender" />
						<SelectItem value={RemoteContentRuleScope.Domain} text="Domain" />
					</Select>
					<TextInput
						id="remote-content-rule-value"
						labelText={
							ruleScope === RemoteContentRuleScope.Sender
								? "Email address"
								: "Domain"
						}
						value={ruleValue}
						onChange={(event) => setRuleValue(event.target.value)}
						required
					/>
					<Button size="sm" type="submit">
						Add rule
					</Button>
				</form>
				{remoteContentRules.data?.length ? (
					<StructuredListWrapper>
						<StructuredListBody>
							{remoteContentRules.data.map((rule) => (
								<StructuredListRow key={rule.id}>
									<StructuredListCell>
										{rule.decision === RemoteContentRuleDecision.Allow
											? "Allow"
											: "Block"}{" "}
										{rule.scope === RemoteContentRuleScope.Sender
											? "sender"
											: "domain"}
									</StructuredListCell>
									<StructuredListCell>{rule.value}</StructuredListCell>
									<StructuredListCell>
										<Button
											size="sm"
											kind="ghost"
											onClick={() => void deleteRule(rule.id)}
										>
											Remove
										</Button>
									</StructuredListCell>
								</StructuredListRow>
							))}
						</StructuredListBody>
					</StructuredListWrapper>
				) : (
					<p className={styles.empty}>No remote-content rules yet.</p>
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
