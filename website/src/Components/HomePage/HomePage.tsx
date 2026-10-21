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
					<p className={styles.eyebrow}>Free, open-source mail and calendar.</p>
					<h1 id="homeHeading">
						All your accounts.
						<br />
						Each in its own place.
					</h1>
					<p className={styles.heroIntro}>
						A calmer home for your email and calendars on Windows, macOS, and
						Linux. Keep work, personal, and shared accounts together without
						mixing them up.
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
					<p className={styles.eyebrow}>Why Mylo Mail</p>
					<h2 id="principleHeading">Everything’s included.</h2>
				</div>
				<dl className={styles.promiseList}>
					<div>
						<dt>Completely free.</dt>
						<dd>
							Completely free to use. No subscriptions, paid tiers, or features
							held back behind an upgrade.
						</dd>
					</div>
					<div>
						<dt>Your inbox is not ad space.</dt>
						<dd>
							No ads added to your workspace and no advertising trackers. Mylo
							Mail is here to help you handle your email, not profile you.
						</dd>
					</div>
					<div>
						<dt>Development you can see.</dt>
						<dd>
							The source code and development history are public. Look under the
							hood, follow changes, and help shape what comes next.
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
					<p className={styles.eyebrow}>Inside Mylo Mail</p>
					<h2 id="featuresHeading">For busy inboxes and full calendars.</h2>
				</div>
				<div className={styles.featureGrid}>
					<article className={styles.feature}>
						<div className={styles.iconWrap}>
							<Icon name="mail" />
						</div>
						<h3>Every account in context</h3>
						<p>
							Work, personal, and shared mail each keep their own place. See
							accounts, folders, and unread counts in one persistent sidebar,
							without disappearing behind an account switcher.
						</p>
					</article>
					<article className={styles.feature}>
						<div className={styles.iconWrap}>
							<Icon name="calendar" />
						</div>
						<h3>Calendars alongside your mail</h3>
						<p>
							Bring supported calendars into a single view while keeping their
							account context. Move between conversations and plans without
							moving to another app.
						</p>
					</article>
					<article className={styles.feature}>
						<div className={styles.iconWrap}>
							<Icon name="layers" />
						</div>
						<h3>Find the conversation</h3>
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
							Read in the pane or open a message in its own window. Compose
							replies, save drafts, and work with attachments without losing
							your place in the inbox.
						</p>
					</article>
					<article className={styles.feature}>
						<div className={styles.iconWrap}>
							<Icon name="layers" />
						</div>
						<h3>A workspace that fits</h3>
						<p>
							Choose light or dark, resize your panes, and keep the layout
							comfortable for the way you work, with a consistent design across
							desktop platforms.
						</p>
					</article>
					<article className={styles.feature}>
						<div className={styles.iconWrap}>
							<Icon name="check" />
						</div>
						<h3>Keep the original</h3>
						<p>
							Export messages as original .eml files, including their headers
							and attachments. Keep a copy outside the app, instead of relying
							on a reconstructed message.
						</p>
					</article>
				</div>
			</section>

			<section className={styles.principle} aria-labelledby="privacyHeading">
				<div className={styles.sectionLead}>
					<p className={styles.eyebrow}>Privacy is part of the design</p>
					<h2 id="privacyHeading">Your mail doesn’t need another middleman.</h2>
				</div>
				<div className={styles.principleCopy}>
					<p>
						Mylo Mail connects directly to the account services you choose.
						Messages and settings are stored on your device, not copied to a
						Mylo Mail cloud service. There is no extra Mylo Mail account to
						create.
					</p>
					<p>
						Remote message images are blocked by default, so opening an email
						doesn’t automatically load a sender’s tracking pixel. You decide
						when to allow remote content.
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
					<p className={styles.eyebrow}>No need to move your email</p>
					<h2 id="compatibilityHeading">Bring the accounts you already use.</h2>
					<p className={styles.sectionIntro}>
						Connect Gmail, Microsoft 365, or an IMAP mailbox. Your provider
						stays your provider; Mylo Mail gives you a desktop home for it.
						Calendar support depends on your account and service.
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
					<p className={styles.eyebrow}>Cross-platform by design</p>
					<h2 id="platformsHeading">Your desktop. Your choice.</h2>
					<p className={styles.sectionIntro}>
						Built for Windows, macOS, and Linux, with a familiar workspace on
						each. Pick your operating system, not a different mail client.
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
					<p className={styles.eyebrow}>Open source</p>
					<h2 id="sourceHeading">
						Your email client shouldn’t be a black box.
					</h2>
				</div>
				<div className={styles.principleCopy}>
					<p>
						The code is public on GitHub. See how Mylo Mail works, read about
						its architecture, and follow development as it happens. You don’t
						have to take the website’s word for how things work.
					</p>
					<p>
						You don’t need to write code to help. Report a bug, describe a
						workflow that could be better, or suggest an accessibility
						improvement. Useful feedback starts with the way you use email.
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
					<p className={styles.eyebrow}>A few useful answers</p>
					<h2 id="faqHeading">Before you make yourself at home.</h2>
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
							No. Mylo Mail is a client for your existing accounts, not a mail
							hosting service. Connect Gmail, Microsoft 365, or IMAP and keep
							using your current address. There is no separate Mylo Mail login.
						</p>
					</details>
					<details>
						<summary>Which computers does it support?</summary>
						<p>
							Mylo Mail is being built for Windows, macOS, and Linux. It is a
							desktop application, not a browser-based inbox or a mobile app.
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
				<p className={styles.eyebrow}>A little more space for what matters</p>
				<h2 id="closingHeading">
					Your accounts. Your desktop. No subscription.
				</h2>
				<p>
					Free, open-source mail and calendar. Follow the project, take a look
					at the code, and help make a better everyday inbox.
				</p>
				<a className={styles.textLink} href={sourceRepository}>
					Follow Mylo Mail on GitHub <Icon name="arrow" />
				</a>
			</section>
		</main>
	);
}
