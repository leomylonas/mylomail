import styles from "@mylomail/website/Components/NotFoundPage/NotFoundPage.module.css";

export function NotFoundPage() {
	return (
		<main id="main-content" className={styles.page}>
			<p>404</p>
			<h1>This page is not here.</h1>
			<p>
				The address may have changed, but there is still a place for your
				accounts.
			</p>
			<a href="/">Back to Mylo Mail</a>
		</main>
	);
}
