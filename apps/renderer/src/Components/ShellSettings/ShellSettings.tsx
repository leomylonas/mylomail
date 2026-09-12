import { useForm } from "@tanstack/react-form";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { z } from "zod";
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
import { RemoteContentRuleDtoSchema } from "@mylomail/shared-types/Api/Contracts/RemoteContentRuleDto";
import { CredentialStoreStatusDtoSchema } from "@mylomail/shared-types/Api/Contracts/CredentialStoreStatusDto";
import {
	PutRemoteContentRuleRequestSchema,
	type PutRemoteContentRuleRequest,
} from "@mylomail/shared-types/Api/Contracts/PutRemoteContentRuleRequest";
import { ShellSettingsDtoSchema } from "@mylomail/shared-types/Api/Contracts/ShellSettingsDto";
import { RemoteContentRuleDecision } from "@mylomail/shared-types/Api/Domain/RemoteContentRuleDecision";
import { RemoteContentRuleScope } from "@mylomail/shared-types/Api/Domain/RemoteContentRuleScope";
import {
	fetchApi,
	fetchApiJson,
	notificationForError,
} from "@mylomail/renderer/Shell/Backend/ProblemDetailsTransport";
import styles from "@mylomail/renderer/Components/ShellSettings/ShellSettings.module.css";
import { notify } from "@mylomail/renderer/Shell/Registries/Notifications/NotificationStore";
import { useWindowNotifications } from "@mylomail/renderer/Shell/Registries/Notifications/UseNotifications";

/** Mirrors server/MyloMail.Api/Domain/AppSettings.cs's CloseBehavior/ThemePreference enum
 * ordinals — read over REST, not the typed SignalR hub, so no JsonStringEnumConverter
 * applies and these arrive as numbers. */
const closeBehavior = { QuitApp: 0, MinimizeToTray: 1 } as const;
const theme = { System: 0, Light: 1, Dark: 2 } as const;
const defaultRule: PutRemoteContentRuleRequest = {
	scope: RemoteContentRuleScope.Sender,
	decision: RemoteContentRuleDecision.Allow,
	value: "",
};

/**
 * The app-wide settings §13 Epic 8 calls for that are not per-account: theme, close
 * behaviour, credential-storage visibility, and persisted sender/domain remote-content
 * allow/block rules (§13 Epic 5). Per-account settings live in `AccountSettings`.
 */
export function ShellSettings({ onClose }: { onClose: () => void }) {
	const queryClient = useQueryClient();
	const { store: notifications } = useWindowNotifications();

	const reportFailure = (title: string) => (error: unknown) =>
		notify(notifications, notificationForError(error, title));

	const settings = useQuery({
		queryKey: ["shell-settings", "app-settings"],
		queryFn: () => fetchApiJson("/shell-settings", ShellSettingsDtoSchema),
	});

	const credentialStore = useQuery({
		queryKey: ["credential-store-status"],
		queryFn: () =>
			fetchApiJson("/credential-store/status", CredentialStoreStatusDtoSchema),
	});

	const remoteContentRules = useQuery({
		queryKey: ["remote-content-rules"],
		queryFn: () =>
			fetchApiJson(
				"/remote-content/rules",
				z.array(RemoteContentRuleDtoSchema),
			),
	});

	// Every shell-settings-derived query gets invalidated together: the three settings above
	// all read the one GET /shell-settings row (or a sibling endpoint), so one write
	// invalidates the lot rather than each caller tracking which fields it touched.
	const invalidateShellSettings = () =>
		void queryClient.invalidateQueries({ queryKey: ["shell-settings"] });

	const setCloseBehavior = async (value: number) => {
		try {
			await fetchApi("/shell-settings/close-behavior", {
				method: "PUT",
				headers: { "Content-Type": "application/json" },
				body: JSON.stringify({ closeBehavior: value }),
			});
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
			await fetchApi("/shell-settings/theme", {
				method: "PUT",
				headers: { "Content-Type": "application/json" },
				body: JSON.stringify({ theme: value }),
			});
			invalidateShellSettings();
		} catch (error) {
			reportFailure("The theme could not be saved")(error);
		}
	};

	const deleteRule = async (id: string) => {
		try {
			await fetchApi(`/remote-content/rules/${id}`, {
				method: "DELETE",
			});
			void queryClient.invalidateQueries({
				queryKey: ["remote-content-rules"],
			});
		} catch (error) {
			reportFailure("The remote-content rule could not be removed")(error);
		}
	};

	const ruleForm = useForm({
		defaultValues: defaultRule,
		validators: {
			onSubmit: PutRemoteContentRuleRequestSchema.extend({
				value: z.string().trim().min(1, "Enter an email address or domain."),
			}),
		},
		onSubmit: async ({ value }) => {
			try {
				await fetchApi("/remote-content/rules", {
					method: "PUT",
					headers: { "Content-Type": "application/json" },
					body: JSON.stringify({ ...value, value: value.value.trim() }),
				});
				ruleForm.reset();
				void queryClient.invalidateQueries({
					queryKey: ["remote-content-rules"],
				});
			} catch (error) {
				reportFailure("The remote-content rule could not be saved")(error);
			}
		},
	});

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
						void ruleForm.handleSubmit();
					}}
				>
					<ruleForm.Field name="decision">
						{(field) => (
							<Select
								id="remote-content-rule-decision"
								labelText="Decision"
								value={field.state.value}
								onBlur={field.handleBlur}
								onChange={(event) =>
									field.handleChange(Number(event.target.value))
								}
							>
								<SelectItem
									value={RemoteContentRuleDecision.Allow}
									text="Allow"
								/>
								<SelectItem
									value={RemoteContentRuleDecision.Block}
									text="Block"
								/>
							</Select>
						)}
					</ruleForm.Field>
					<ruleForm.Field name="scope">
						{(field) => (
							<Select
								id="remote-content-rule-scope"
								labelText="Applies to"
								value={field.state.value}
								onBlur={field.handleBlur}
								onChange={(event) =>
									field.handleChange(Number(event.target.value))
								}
							>
								<SelectItem
									value={RemoteContentRuleScope.Sender}
									text="Sender"
								/>
								<SelectItem
									value={RemoteContentRuleScope.Domain}
									text="Domain"
								/>
							</Select>
						)}
					</ruleForm.Field>
					<ruleForm.Field name="value">
						{(field) => (
							<ruleForm.Subscribe selector={(state) => state.values.scope}>
								{(scope) => (
									<TextInput
										id="remote-content-rule-value"
										name={field.name}
										labelText={
											scope === RemoteContentRuleScope.Sender
												? "Email address"
												: "Domain"
										}
										value={field.state.value}
										onBlur={field.handleBlur}
										onChange={(event) => field.handleChange(event.target.value)}
										required
									/>
								)}
							</ruleForm.Subscribe>
						)}
					</ruleForm.Field>
					<ruleForm.Subscribe
						selector={(state) => ({
							canSubmit: state.canSubmit,
							isSubmitting: state.isSubmitting,
							value: state.values.value,
						})}
					>
						{({ canSubmit, isSubmitting, value }) => (
							<Button
								size="sm"
								type="submit"
								disabled={!canSubmit || isSubmitting || !String(value).trim()}
							>
								Add rule
							</Button>
						)}
					</ruleForm.Subscribe>
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
