import type { ReactNode } from "react";
import styles from "@mylomail/website/Components/SiteShell/SiteShell.module.css";

export function SiteShell({ children }: { children: ReactNode }) {
	return (
		<div className={styles.shell}>
			<a className={styles.skipLink} href="#main-content">
				Skip to content
			</a>
			<header className={styles.header}>
				<div className={styles.headerInner}>
					<a className={styles.brand} href="/" aria-label="Mylo Mail home">
						<picture>
							<source
								media="(prefers-color-scheme: dark)"
								srcSet="/Brand/HorizontalDark.svg"
							/>
							<img
								src="/Brand/HorizontalLight.svg"
								width="334"
								height="142"
								alt="Mylo Mail"
							/>
						</picture>
					</a>
					<nav className={styles.navigation} aria-label="Main navigation">
						<a href="/#features">Features</a>
						<a href="/privacy/">Privacy</a>
					</nav>
				</div>
			</header>
			{children}
			<footer className={styles.footer}>
				<div className={styles.footerInner}>
					<div>
						<strong>Mylo Mail</strong>
						<p>Open-source mail and calendar for the desktop.</p>
					</div>
					<nav aria-label="Footer navigation">
						<a href="/privacy/">Privacy policy</a>
						<a href="/privacy/#contact">Contact</a>
					</nav>
				</div>
			</footer>
		</div>
	);
}
