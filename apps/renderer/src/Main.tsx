import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import { BackendStatus } from "@mylomail/renderer/Shell/BackendStatus/BackendStatus";

export const applicationName = "MyloMail";

const container = document.getElementById("root");
if (!container) throw new Error("The renderer root element is missing.");

createRoot(container).render(
	<StrictMode>
		<BackendStatus />
	</StrictMode>,
);
