import styles from "@mylomail/website/Components/NotFoundPage/NotFoundPage.module.css";

export function NotFoundPage() {
	return (
		<main id="main-content" className={styles.page}>
			<p>404</p>
			<h1>Page not found</h1>
			<p>Check the address or return to the home page.</p>
			<a href="/">Back to Mylo Mail</a>
		</main>
	);
}
