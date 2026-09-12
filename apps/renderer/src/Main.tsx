import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import { AppRouter } from "@mylomail/renderer/Shell/AppRouter/AppRouter";
import { WindowScope } from "@mylomail/renderer/Shell/WindowScope/WindowScope";
import { ThemeProvider } from "@mylomail/renderer/Shell/ThemeProvider/ThemeProvider";
import "@mylomail/renderer/Styles/Carbon.scss";

export const applicationName = "MyloMail";

const container = document.getElementById("root");
if (!container) throw new Error("The renderer root element is missing.");

createRoot(container).render(
	<StrictMode>
		<WindowScope>
			<ThemeProvider>
				<AppRouter />
			</ThemeProvider>
		</WindowScope>
	</StrictMode>,
);
