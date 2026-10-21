import type { ReactNode } from "react";
import styles from "@mylomail/website/Components/SiteShell/SiteShell.module.css";
import { sourceRepository } from "@mylomail/website/Lib/Site";

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
						<a href="/#why-mylo">Why Mylo</a>
						<a href="/#features">Features</a>
						<a href="/#open-source">Source code</a>
						<a href="/#faq">FAQ</a>
					</nav>
				</div>
			</header>
			{children}
			<footer className={styles.footer}>
				<div className={styles.footerInner}>
					<div>
						<strong>Mylo Mail</strong>
						<p>Free, open-source mail and calendar.</p>
					</div>
					<nav aria-label="Footer navigation">
						<a href="/#platforms">Platforms</a>
						<a href={sourceRepository}>Source code</a>
						<a href="/privacy/">Privacy policy</a>
						<a href="/privacy/#contact">Contact</a>
					</nav>
				</div>
			</footer>
		</div>
	);
}
