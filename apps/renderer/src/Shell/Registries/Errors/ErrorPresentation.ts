import { ErrorCategory } from "@mylomail/shared-types/SignalR/MyloMail.Api.Errors";

/** How one failure should be presented, derived from its category alone. */
export interface ErrorPresentation {
	title: string;
	detail: string;

	/**
	 * Whether the user can do something about it now.
	 *
	 * Drives which Carbon component is used: a toast that carries an action breaks WCAG,
	 * because it dismisses itself while the action is still reachable by keyboard. Anything
	 * actionable becomes an ActionableNotification instead (§13).
	 */
	action?: "reauthenticate" | "retry" | "resolve";

	/** Whether it disappears on its own. A failure the user must act on does not.  */
	transient: boolean;
}

/**
 * The single mapping from error category to what the user sees (§13, §15).
 *
 * <b>One mapping, not one per call site.</b> Every failure in the system already carries a
 * category, and the value of that taxonomy is entirely lost if each screen decides for itself
 * what "RateLimit" means — the same failure would read as three different problems depending
 * on where it surfaced.
 */
export function present(
	category: ErrorCategory,
	detail: string | null | undefined,
): ErrorPresentation {
	switch (category) {
		case ErrorCategory.Network:
			// Not the user's problem to solve, and not worth a persistent alarm: connectivity
			// comes back on its own and the work resumes (§15).
			return {
				title: "You appear to be offline",
				detail: detail ?? "MyloMail will carry on once the connection returns.",
				transient: true,
			};

		case ErrorCategory.Auth:
			return {
				title: "Sign in again",
				detail: detail ?? "This account needs to be reauthenticated.",
				action: "reauthenticate",
				transient: false,
			};

		case ErrorCategory.RateLimit:
			return {
				title: "Slowed down by the provider",
				detail:
					detail ?? "MyloMail is waiting the time the provider asked for.",
				transient: true,
			};

		case ErrorCategory.Conflict:
			// Detect, never merge: the user decides which version survives (§1, §15).
			return {
				title: "Changed somewhere else",
				detail:
					detail ?? "This was changed elsewhere. Choose which version to keep.",
				action: "resolve",
				transient: false,
			};

		case ErrorCategory.Validation:
		case ErrorCategory.ProviderRejected:
			return {
				title: "The server refused this",
				detail: detail ?? "The change was not applied.",
				action: "retry",
				transient: false,
			};

		case ErrorCategory.Unknown:
		default:
			return {
				title: "Something went wrong",
				detail: detail ?? "The change was not applied.",
				action: "retry",
				transient: false,
			};
	}
}
