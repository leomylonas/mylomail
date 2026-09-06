import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import { AppShell } from "@mylomail/renderer/Shell/AppShell/AppShell";
import { MessageWindow } from "@mylomail/renderer/Shell/Windows/MessageWindow/MessageWindow";
import { ComposeWindow } from "@mylomail/renderer/Shell/Windows/ComposeWindow/ComposeWindow";
import { WindowScope } from "@mylomail/renderer/Shell/WindowScope/WindowScope";
import { ThemeProvider } from "@mylomail/renderer/Shell/ThemeProvider/ThemeProvider";
import { parseWindowRoute } from "@mylomail/renderer/Shell/WindowRoute";
import "@carbon/react/index.scss";

export const applicationName = "MyloMail";

const container = document.getElementById("root");
if (!container) throw new Error("The renderer root element is missing.");

/**
 * Every window loads this same document (§13 Epic 10) — there is no separate entry point per
 * window kind, only a query string the shell attaches when it opens one. Absent, this is an
 * ordinary main window; present, it is a single message or a popped-out draft and nothing else.
 */
function chooseRoot() {
	const route = parseWindowRoute(window.location.search);
	switch (route.kind) {
		case "message":
			return (
				<MessageWindow
					messageId={route.messageId}
					subject={route.subject}
					senderAddress={route.senderAddress}
				/>
			);
		case "compose":
			return (
				<ComposeWindow draftId={route.draftId} accountId={route.accountId} />
			);
		case "shell":
			return <AppShell />;
	}
}

createRoot(container).render(
	<StrictMode>
		<WindowScope>
			<ThemeProvider>{chooseRoot()}</ThemeProvider>
		</WindowScope>
	</StrictMode>,
);
