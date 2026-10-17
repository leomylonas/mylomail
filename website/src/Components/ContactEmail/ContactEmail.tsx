"use client";

import { useId, useState } from "react";
import styles from "@mylomail/website/Components/ContactEmail/ContactEmail.module.css";

export function ContactEmail() {
	const [revealed, setRevealed] = useState(false);
	const addressId = useId();
	// A human-triggered reveal deters simple HTML harvesters, not browser-capable bots.
	const address = revealed
		? ["info", ["mylo", "software"].join(".")].join("@")
		: null;

	return (
		<div className={styles.contact}>
			<button
				type="button"
				className={styles.reveal}
				aria-controls={addressId}
				aria-expanded={revealed}
				onClick={() => setRevealed(true)}
			>
				Show contact email
			</button>
			<div id={addressId} className={styles.address} aria-live="polite">
				{address && <a href={`mailto:${address}`}>{address}</a>}
			</div>
			<noscript>Enable JavaScript to reveal the contact email.</noscript>
		</div>
	);
}
