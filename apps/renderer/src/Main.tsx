import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import { AppShell } from "@mylomail/renderer/Shell/AppShell/AppShell";
import { WindowScope } from "@mylomail/renderer/Shell/WindowScope/WindowScope";
import "@carbon/react/index.scss";

export const applicationName = "MyloMail";

const container = document.getElementById("root");
if (!container) throw new Error("The renderer root element is missing.");

createRoot(container).render(
	<StrictMode>
		<WindowScope>
			<AppShell />
		</WindowScope>
	</StrictMode>,
);
