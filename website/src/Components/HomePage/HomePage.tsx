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

function ProductIllustration() {
	return (
		<figure
			className={styles.productFigure}
			aria-labelledby="illustrationCaption"
		>
			<div className={styles.illustration}>
				<div className={styles.windowBar}>
					<span className={styles.windowTitle}>Mylo Mail</span>
					<span className={styles.windowStatus}>Illustration</span>
				</div>
				<div className={styles.workspace}>
					<aside className={styles.accountRail} aria-hidden="true">
						<div
							className={`${styles.accountDot} ${styles.accountDotPrimary}`}
						/>
						<div className={styles.accountDot} />
						<div className={styles.accountDot} />
					</aside>
					<section className={styles.inboxPanel} aria-hidden="true">
						<p className={styles.panelLabel}>Accounts</p>
						<div className={styles.accountName}>Studio</div>
						<div className={styles.mailboxActive}>
							Inbox <span>3</span>
						</div>
						<div className={styles.mailbox}>Later</div>
						<div className={styles.mailbox}>Archive</div>
						<div className={styles.accountName}>Personal</div>
						<div className={styles.mailbox}>
							Inbox <span>1</span>
						</div>
						<div className={styles.accountName}>Volunteer work</div>
						<div className={styles.mailbox}>Inbox</div>
					</section>
					<section className={styles.messagePanel} aria-hidden="true">
						<div className={styles.messageHeading}>
							<div>
								<p className={styles.panelLabel}>Studio / Inbox</p>
								<strong>3 conversations</strong>
							</div>
							<span className={styles.messageCount}>Today</span>
						</div>
						<div
							className={`${styles.messageRow} ${styles.messageRowSelected}`}
						>
							<span className={styles.avatar}>A</span>
							<span>
								<strong>Aster Workshop</strong>
								<small>Thursday’s materials</small>
							</span>
							<time>10:28</time>
						</div>
						<div className={styles.messageRow}>
							<span className={styles.avatar}>M</span>
							<span>
								<strong>Marin</strong>
								<small>Notes from the review</small>
							</span>
							<time>09:41</time>
						</div>
						<div className={styles.messageRow}>
							<span className={styles.avatar}>T</span>
							<span>
								<strong>Team calendar</strong>
								<small>Planning session</small>
							</span>
							<time>08:15</time>
						</div>
					</section>
					<section className={styles.detailPanel} aria-hidden="true">
						<p className={styles.panelLabel}>Aster Workshop</p>
						<h3>Thursday’s materials</h3>
						<p className={styles.detailMeta}>To Studio · 10:28</p>
						<div className={styles.detailRule} />
						<p className={styles.detailCopy}>Hi Studio,</p>
						<p className={styles.detailCopy}>
							Here is the reading list for Thursday. The agenda is in your
							calendar too.
						</p>
						<div className={styles.eventNote}>
							<Icon name="calendar" />
							<span>
								<strong>Planning session</strong>
								<small>Thursday · 14:00</small>
							</span>
						</div>
					</section>
				</div>
			</div>
			<figcaption id="illustrationCaption">
				Illustration with fictional account and message names.
			</figcaption>
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
				<ProductIllustration />
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
