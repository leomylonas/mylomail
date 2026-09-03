// CloseBehavior.MinimizeToTray = 1 (server/MyloMail.Api/Domain/AppSettings.cs) —
// System.Text.Json serialises enums as their numeric ordinal by default here, since
// AppSettingsController has no JsonStringEnumConverter registered.
export function closeBehaviorFromValue(
	value: unknown,
): "QuitApp" | "MinimizeToTray" {
	return value === 1 ? "MinimizeToTray" : "QuitApp";
}
