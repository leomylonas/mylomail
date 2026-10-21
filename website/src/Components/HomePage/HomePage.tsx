import styles from "@mylomail/website/Components/HomePage/HomePage.module.css";
import { sourceRepository } from "@mylomail/website/Lib/Site";

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
					<p className={styles.eyebrow}>Free, open-source email and calendar</p>
					<h1 id="homeHeading">
						All your accounts.
						<br />
						Each in its own place.
					</h1>
					<p className={styles.heroIntro}>
						Work mail, personal mail, and the calendars that go with them. Mylo
						Mail brings them together on Windows, macOS, and Linux, with each
						account’s folders and unread counts in view.
					</p>
					<div className={styles.heroActions}>
						<a className={styles.primaryLink} href="#features">
							Explore the features <Icon name="arrow" />
						</a>
						<a className={styles.secondaryLink} href={sourceRepository}>
							View source on GitHub
						</a>
					</div>
					<p className={styles.status}>
						<span aria-hidden="true" />
						In active development
					</p>
					<ul className={styles.highlights} aria-label="At a glance">
						<li>Completely free</li>
						<li>No subscriptions</li>
						<li>No ads or advertising trackers</li>
					</ul>
				</div>
				<ProductPreview />
			</section>

			<section
				id="why-mylo"
				className={styles.principle}
				aria-labelledby="principleHeading"
			>
				<div className={styles.sectionLead}>
					<p className={styles.eyebrow}>Free to use</p>
					<h2 id="principleHeading">Everything’s included.</h2>
				</div>
				<dl className={styles.promiseList}>
					<div>
						<dt>No subscriptions or paid upgrades</dt>
						<dd>Mylo Mail is completely free. Every feature is included.</dd>
					</div>
					<div>
						<dt>No ads or advertising trackers</dt>
						<dd>
							The app contains no ads or advertising trackers. This website also
							has no analytics or tracking cookies.
						</dd>
					</div>
					<div>
						<dt>Open source</dt>
						<dd>
							The code is available under the MIT licence. You can read, modify,
							and share it.
						</dd>
					</div>
				</dl>
			</section>

			<section
				id="features"
				className={styles.featureSection}
				aria-labelledby="featuresHeading"
			>
				<div className={styles.sectionLead}>
					<p className={styles.eyebrow}>Features</p>
					<h2 id="featuresHeading">For busy inboxes and full calendars.</h2>
				</div>
				<div className={styles.featureGrid}>
					<article className={styles.feature}>
						<div className={styles.iconWrap}>
							<Icon name="mail" />
						</div>
						<h3>Every account at hand</h3>
						<p>
							Keep work, personal, and shared accounts in the same sidebar. Each
							has its own folders, unread counts, and sync status.
						</p>
					</article>
					<article className={styles.feature}>
						<div className={styles.iconWrap}>
							<Icon name="calendar" />
						</div>
						<h3>Calendars alongside your mail</h3>
						<p>
							View supported calendars together and see which account each
							belongs to. Switch between mail and calendar views in the app.
						</p>
					</article>
					<article className={styles.feature}>
						<div className={styles.iconWrap}>
							<Icon name="layers" />
						</div>
						<h3>Find the message you need</h3>
						<p>
							Search your mail, narrow the message list with filters, and use
							flags to keep important messages easy to return to.
						</p>
					</article>
					<article className={styles.feature}>
						<div className={styles.iconWrap}>
							<Icon name="mail" />
						</div>
						<h3>Room to read and reply</h3>
						<p>
							Read messages in the pane or open them in separate windows.
							Compose replies, save drafts, and add attachments.
						</p>
					</article>
					<article className={styles.feature}>
						<div className={styles.iconWrap}>
							<Icon name="layers" />
						</div>
						<h3>Make yourself comfortable</h3>
						<p>
							Choose a light or dark theme and resize the panes to suit your
							screen. The interface is consistent across desktop platforms.
						</p>
					</article>
					<article className={styles.feature}>
						<div className={styles.iconWrap}>
							<Icon name="check" />
						</div>
						<h3>Keep a copy for later</h3>
						<p>
							Save messages as original .eml files with their headers and
							attachments. Open them in another mail app or keep them as a local
							archive.
						</p>
					</article>
				</div>
			</section>

			<section className={styles.principle} aria-labelledby="privacyHeading">
				<div className={styles.sectionLead}>
					<p className={styles.eyebrow}>Privacy</p>
					<h2 id="privacyHeading">Know where your mail goes.</h2>
				</div>
				<div className={styles.principleCopy}>
					<p>
						Mylo Mail connects to the account services you configure and stores
						messages and settings on your device. You sign in with your existing
						accounts.
					</p>
					<p>
						Remote images are blocked by default. You choose when to load them,
						since loading an image can tell its sender that you opened the
						message.
					</p>
					<p>
						This website has no analytics or tracking cookies. The app’s remote
						diagnostic telemetry is off by default. For details about local
						storage, providers, and optional diagnostics, read our{" "}
						<a href="/privacy/">privacy policy</a>.
					</p>
				</div>
			</section>

			<section
				id="accounts"
				className={styles.compatibility}
				aria-labelledby="compatibilityHeading"
			>
				<div>
					<p className={styles.eyebrow}>Account support</p>
					<h2 id="compatibilityHeading">Bring the accounts you already use.</h2>
					<p className={styles.sectionIntro}>
						Keep your existing email addresses and providers. Calendar support
						depends on your account and service.
					</p>
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

			<section
				id="platforms"
				className={styles.platformSection}
				aria-labelledby="platformsHeading"
			>
				<div className={styles.sectionLead}>
					<p className={styles.eyebrow}>Desktop platforms</p>
					<h2 id="platformsHeading">At home on Windows, macOS, and Linux.</h2>
					<p className={styles.sectionIntro}>
						The desktop app uses the same interface across all three platforms.
					</p>
				</div>
				<ul
					className={styles.platformGrid}
					aria-label="Desktop platforms and package formats"
				>
					<li>
						<h3>Windows</h3>
						<p>.exe and .msi installers</p>
					</li>
					<li>
						<h3>macOS</h3>
						<p>.dmg and .zip packages</p>
					</li>
					<li>
						<h3>Linux</h3>
						<p>AppImage, .deb, and .rpm packages</p>
					</li>
				</ul>
				<p className={styles.platformNote}>
					Mylo Mail is in active development. These are the desktop packaging
					targets; follow the project for release news.
				</p>
			</section>

			<section
				id="open-source"
				className={styles.principle}
				aria-labelledby="sourceHeading"
			>
				<div className={styles.sectionLead}>
					<p className={styles.eyebrow}>Source code</p>
					<h2 id="sourceHeading">Take a look behind the scenes.</h2>
				</div>
				<div className={styles.principleCopy}>
					<p>
						Curious how Mylo Mail works? The code, build instructions, and
						development history are on GitHub. The{" "}
						<a href={`${sourceRepository}/blob/main/LICENSE`}>MIT licence</a>{" "}
						allows you to use, modify, and share the software.
					</p>
					<p>
						You can help by reporting bugs, suggesting features, or testing the
						app. Accessibility feedback is welcome too.
					</p>
					<p>
						<a href={sourceRepository}>Explore the source code</a>
						{" · "}
						<a href={`${sourceRepository}/issues`}>Issues and ideas</a>
					</p>
				</div>
			</section>

			<section
				id="faq"
				className={styles.faqSection}
				aria-labelledby="faqHeading"
			>
				<div className={styles.sectionLead}>
					<p className={styles.eyebrow}>FAQ</p>
					<h2 id="faqHeading">Got a question?</h2>
				</div>
				<div className={styles.faqList}>
					<details>
						<summary>Is Mylo Mail really free?</summary>
						<p>
							Yes. Mylo Mail is completely free to use, with no subscriptions,
							paid tiers, or feature unlocks. Your email or calendar provider
							may still charge for its own service.
						</p>
					</details>
					<details>
						<summary>Do I need a new email address?</summary>
						<p>
							No. Connect your existing Gmail, Microsoft 365, or IMAP account
							and keep using your current address. You don’t need a separate
							Mylo Mail account.
						</p>
					</details>
					<details>
						<summary>Which computers does it support?</summary>
						<p>
							Mylo Mail is a desktop application for Windows, macOS, and Linux.
						</p>
					</details>
					<details>
						<summary>Does it show ads or track me?</summary>
						<p>
							Mylo Mail adds no ads or advertising trackers. This website also
							has no analytics or tracking cookies. Remote message images are
							blocked until you allow them, and remote diagnostic telemetry is
							off by default. Your providers and any remote content you choose
							to load have their own data practices; see the{" "}
							<a href="/privacy/">privacy policy</a> for the details.
						</p>
					</details>
					<details>
						<summary>Where can I see the code or help out?</summary>
						<p>
							Visit the <a href={sourceRepository}>GitHub repository</a> to read
							the code and build instructions. Use the{" "}
							<a href={`${sourceRepository}/issues`}>issue tracker</a> for bugs
							and ideas. Please don’t include private messages, passwords, or
							account tokens in a public issue.
						</p>
					</details>
					<details>
						<summary>Can I download it yet?</summary>
						<p>
							There are no published releases yet. Mylo Mail is in active
							development; visit the{" "}
							<a href={sourceRepository}>project on GitHub</a> for build
							instructions, current progress, and release news.
						</p>
					</details>
				</div>
			</section>

			<section className={styles.closing} aria-labelledby="closingHeading">
				<p className={styles.eyebrow}>In development</p>
				<h2 id="closingHeading">Come see what we’re building.</h2>
				<p>
					Mylo Mail is in active development. Take a look at the code, try a
					build, or tell us what would make your day-to-day email a little
					easier.
				</p>
				<a className={styles.textLink} href={sourceRepository}>
					Follow Mylo Mail on GitHub <Icon name="arrow" />
				</a>
			</section>
		</main>
	);
}
