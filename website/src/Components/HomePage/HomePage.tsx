import styles from "@mylomail/website/Components/HomePage/HomePage.module.css";

type IconName = "mail" | "calendar" | "layers" | "arrow" | "check";

function Icon({ name }: { name: IconName }) {
	if (name === "mail") {
		return (
			<svg aria-hidden="true" viewBox="0 0 24 24">
				<path d="M3.5 6.5h17v11h-17z" />
				<path d="m3.5 7 8.5 6 8.5-6" />
			</svg>
		);
	}

	if (name === "calendar") {
		return (
			<svg aria-hidden="true" viewBox="0 0 24 24">
				<rect x="4" y="5.5" width="16" height="14" rx="1" />
				<path d="M8 3.5v4M16 3.5v4M4 10h16M8 14h3M13 14h3" />
			</svg>
		);
	}

	if (name === "layers") {
		return (
			<svg aria-hidden="true" viewBox="0 0 24 24">
				<path d="m12 3.5 8 4.25L12 12 4 7.75 12 3.5Z" />
				<path d="m4 12 8 4.25L20 12M4 16.25 12 20.5l8-4.25" />
			</svg>
		);
	}

	if (name === "check") {
		return (
			<svg aria-hidden="true" viewBox="0 0 24 24">
				<path d="m5 12.5 4.25 4.25L19 7" />
			</svg>
		);
	}

	return (
		<svg aria-hidden="true" viewBox="0 0 24 24">
			<path d="M5 12h13M13 7l5 5-5 5" />
		</svg>
	);
}

function ProductPreview() {
	return (
		<figure className={styles.productFigure}>
			<picture>
				<source
					media="(prefers-color-scheme: dark)"
					srcSet="/Screenshots/MailDark.png"
				/>
				<img
					className={styles.productImage}
					src="/Screenshots/MailLight.png"
					width="1440"
					height="900"
					decoding="async"
					alt="Mylo Mail with three separate accounts, an inbox, and a workshop email open in the reading pane."
				/>
			</picture>
		</figure>
	);
}

export function HomePage() {
	return (
		<main id="main-content">
			<section className={styles.hero} aria-labelledby="homeHeading">
				<div className={styles.heroCopy}>
					<p className={styles.eyebrow}>A desktop mail and calendar client</p>
					<h1 id="homeHeading">
						All your accounts.
						<br />
						Each in its own place.
					</h1>
					<p className={styles.heroIntro}>
						Mylo Mail keeps every account visible without making them feel mixed
						together. Read mail, see calendars, and move through your day with
						the context that belongs to each account intact.
					</p>
					<div className={styles.heroActions}>
						<a className={styles.primaryLink} href="#features">
							Explore the approach <Icon name="arrow" />
						</a>
						<a className={styles.secondaryLink} href="#inside">
							See what is inside
						</a>
					</div>
					<p className={styles.status}>
						<span aria-hidden="true" />
						In development
					</p>
				</div>
				<ProductPreview />
			</section>

			<section
				id="features"
				className={styles.principle}
				aria-labelledby="principleHeading"
			>
				<div className={styles.sectionLead}>
					<p className={styles.eyebrow}>A clearer way to hold it all</p>
					<h2 id="principleHeading">
						One place to work. A place for every account.
					</h2>
				</div>
				<div className={styles.principleCopy}>
					<p>
						Work, personal, and shared mail all deserve their own bearings. Mylo
						Mail puts every mailbox in view at once, while keeping the folders,
						status, and activity around each account easy to recognise.
					</p>
					<p>
						That means less mental sorting before you can start reading. You can
						stay aware of everything without treating every message as if it
						came from the same place.
					</p>
				</div>
			</section>

			<section
				id="inside"
				className={styles.featureSection}
				aria-labelledby="insideHeading"
			>
				<div className={styles.sectionLead}>
					<p className={styles.eyebrow}>Inside Mylo Mail</p>
					<h2 id="insideHeading">Made for the shape of your day.</h2>
				</div>
				<div className={styles.featureGrid}>
					<article className={styles.feature}>
						<div className={styles.iconWrap}>
							<Icon name="mail" />
						</div>
						<h3>Mail in context</h3>
						<p>
							See all your accounts in one persistent sidebar, with each
							account’s folders and unread counts close at hand.
						</p>
					</article>
					<article className={styles.feature}>
						<div className={styles.iconWrap}>
							<Icon name="calendar" />
						</div>
						<h3>Calendars together</h3>
						<p>
							Bring calendars into a single view while retaining the account
							they belong to, so plans have their useful context.
						</p>
					</article>
					<article className={styles.feature}>
						<div className={styles.iconWrap}>
							<Icon name="layers" />
						</div>
						<h3>Separate by design</h3>
						<p>
							Keep professional, personal, and shared accounts distinct without
							hiding any of them behind a switcher.
						</p>
					</article>
				</div>
			</section>

			<section
				className={styles.compatibility}
				aria-labelledby="compatibilityHeading"
			>
				<div>
					<p className={styles.eyebrow}>Built around your existing accounts</p>
					<h2 id="compatibilityHeading">Bring the accounts you already use.</h2>
				</div>
				<ul
					className={styles.providerList}
					aria-label="Supported account types"
				>
					<li>
						<Icon name="check" /> IMAP
					</li>
					<li>
						<Icon name="check" /> Gmail
					</li>
					<li>
						<Icon name="check" /> Microsoft 365
					</li>
				</ul>
			</section>

			<section className={styles.closing} aria-labelledby="closingHeading">
				<p className={styles.eyebrow}>A considered desktop companion</p>
				<h2 id="closingHeading">
					More room for what matters. Less effort finding where it belongs.
				</h2>
				<p>
					Mylo Mail is being built for people who carry more than one part of
					life in their inbox.
				</p>
				<a className={styles.textLink} href="#features">
					Return to the approach <Icon name="arrow" />
				</a>
			</section>
		</main>
	);
}
