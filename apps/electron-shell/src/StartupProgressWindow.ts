import { BrowserWindow } from "electron";
import type { StartupProgress } from "@mylomail/electron-shell/BackendHealthProbe";
import { getAppIcon } from "@mylomail/electron-shell/AppIcon";

const titles: Record<string, string> = {
	backup: "Backing up your database",
	migrating: "Upgrading your database",
};

/**
 * A small window shown only when the backend reports a long startup phase — an ordinary
 * launch never creates it. The backend has no port to answer on yet, so the progress arrives
 * as lines on its standard output.
 */
export function createStartupProgressWindow(showAfterMs = 2000): {
	update(progress: StartupProgress): void;
	close(): void;
} {
	let window: BrowserWindow | undefined;
	let latest: StartupProgress | undefined;
	let pending: NodeJS.Timeout | undefined;

	const render = (progress: StartupProgress) => {
		if (!window) {
			const page = `<!doctype html>
<meta charset="utf-8">
<title>Mylo Mail</title>
<style>
	html, body { overflow: hidden; }
	body { box-sizing: border-box; height: 100vh; margin: 0; padding: 24px; font: 14px system-ui, sans-serif; background: #262626; color: #f4f4f4; }
	.brand { display: flex; align-items: center; gap: 12px; margin-bottom: 12px; }
	h1 { margin: 0; font-size: 16px; font-weight: 600; }
	p { margin: 12px 0 0; color: #c6c6c6; }
	progress { width: 100%; height: 8px; accent-color: #4589ff; }
</style>
<header class="brand"><img src="${getAppIcon().toDataURL()}" width="32" height="32" alt=""><h1 id="title">Preparing Mylo Mail</h1></header>
<progress id="bar"></progress>
<p id="detail">Your database is being upgraded. This can take a while for a large mailbox.</p>`;
			window = new BrowserWindow({
				useContentSize: true,
				width: 440,
				height: 168,
				resizable: false,
				minimizable: false,
				maximizable: false,
				title: "Mylo Mail",
				icon: getAppIcon(),
				autoHideMenuBar: true,
			});
			void window.loadURL(
				`data:text/html;charset=utf-8,${encodeURIComponent(page)}`,
			);
		}
		const current = window;
		const title = titles[progress.phase] ?? "Preparing Mylo Mail";
		const percent =
			progress.fraction === null ? null : Math.round(progress.fraction * 100);
		current.setProgressBar(progress.fraction ?? 2);
		// Only a fixed title and a number are interpolated, never backend-supplied text.
		void current.webContents
			.executeJavaScript(
				`(() => {
					document.getElementById("title").textContent = ${JSON.stringify(
						percent === null ? title : `${title} — ${percent}%`,
					)};
					const bar = document.getElementById("bar");
					${progress.fraction === null ? 'bar.removeAttribute("value");' : `bar.value = ${progress.fraction};`}
				})();`,
			)
			.catch(() => undefined);
	};

	return {
		update(progress) {
			latest = progress;
			if (window) {
				render(progress);
				return;
			}
			// Most launches migrate in a moment, and a window that flashes up and vanishes is worse
			// than none, so it appears only once startup has genuinely taken a while.
			pending ??= setTimeout(() => {
				if (latest) render(latest);
			}, showAfterMs);
		},
		close() {
			clearTimeout(pending);
			pending = undefined;
			latest = undefined;
			if (window && !window.isDestroyed()) window.destroy();
			window = undefined;
		},
	};
}
