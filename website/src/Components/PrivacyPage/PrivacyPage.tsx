import { ContactEmail } from "@mylomail/website/Components/ContactEmail/ContactEmail";
import styles from "@mylomail/website/Components/PrivacyPage/PrivacyPage.module.css";

export function PrivacyPage() {
	return (
		<main id="main-content" className={styles.page}>
			<header className={styles.introduction}>
				<p className={styles.eyebrow}>A little clarity about your data</p>
				<h1>Privacy policy</h1>
				<p className={styles.lead}>
					This website is a brochure. Mylo Mail is a desktop client, not a
					hosted mailbox.
				</p>
				<p className={styles.updated}>
					Last updated <time dateTime="2026-10-17">17 October 2026</time>
				</p>
			</header>
			<div className={styles.content}>
				<p>
					This policy describes{" "}
					<a href="https://mylo.software/">mylo.software</a> and the standard
					Mylo Mail desktop application. Mylo Mail is an open-source project.
					Independently modified builds, their distributors, and your account
					providers may have different data practices.
				</p>
				<nav className={styles.contents} aria-label="On this page">
					<a href="#website">This website</a>
					<a href="#local-data">Data on your device</a>
					<a href="#providers">Account providers</a>
					<a href="#diagnostics">Diagnostics</a>
					<a href="#your-control">Your control</a>
					<a href="#contact">Contact</a>
				</nav>
				<section id="website">
					<h2>This website</h2>
					<p>
						The website has no analytics, advertising trackers, tracking
						cookies, signup forms, or contact forms. Its fonts and images are
						served with the website, not requested from third-party font or
						image services. Revealing the contact email happens in your browser
						and does not submit a form.
					</p>
					<p>
						Serving a page still requires a network connection. The hosting
						service receives information such as your IP address, requested URL,
						browser information, and request time. Hosting providers may keep
						access or security logs under their own policies and configuration.
						This site does not turn those requests into advertising profiles.
					</p>
					<p>
						Following an external link takes you to a service with its own
						privacy practices. Sending an email opens your chosen mail
						application; it is not a message submitted through this website.
					</p>
				</section>
				<section id="local-data">
					<h2>Data on your device</h2>
					<p>
						The desktop app stores account settings, messages and original
						message content, attachments, drafts, calendars, contacts, search
						indexes, synchronisation state, and preferences locally. It uses a
						database in your operating system’s application-data directory.
						Exports are saved to a location you choose.
					</p>
					<p>
						Mylo Mail does not provide a hosted account or a project-operated
						mail relay. The app connects to the account services you configure.
						It does not upload a copy of your mailbox to this brochure website.
					</p>
					<p>
						The mail and calendar database is not encrypted by Mylo Mail.
						Protect it with your operating system’s account controls, disk
						encryption, and backup settings. Credential protection, described
						below, does not encrypt the rest of the database.
					</p>
					<h3>Passwords and sign-in tokens</h3>
					<p>
						Credentials and OAuth token caches use the operating system’s
						protected credential storage when available: Windows data
						protection, macOS Keychain, or the Linux Secret Service. If native
						storage is unavailable, the app requires a master password and uses
						an encrypted local credential store. There is no silent plaintext
						credential fallback.
					</p>
				</section>
				<section id="providers">
					<h2>Your account providers</h2>
					<p>
						Mylo Mail communicates directly with the services you select: IMAP
						and SMTP servers, Google APIs, Microsoft Graph, and configured
						CalDAV servers. Those services receive the information needed to
						authenticate, synchronise, read, change, send, or delete your mail
						and, where supported, calendars and contacts.
					</p>
					<p>
						Google and Microsoft accounts use their sign-in and consent flows.
						Permissions can cover mail access and sending, calendar access, and
						contacts. Permanent Gmail deletion can require full mail access;
						Microsoft 365 shared mailboxes use delegated shared-mailbox
						permissions. Some builds require an application registration or
						administrator consent before these account types can be used.
					</p>
					<p>
						See{" "}
						<a href="https://policies.google.com/privacy">
							Google’s privacy policy
						</a>
						,{" "}
						<a href="https://privacy.microsoft.com/privacystatement">
							Microsoft’s privacy statement
						</a>
						, and the policies of your other mail or calendar providers. You can
						review or revoke account authorisations through the relevant
						provider.
					</p>
					<h3>Google user data</h3>
					<p>
						Data received from Google APIs is used to provide the mail,
						calendar, and contacts features you choose. Mylo Mail does not use
						that data for advertising, sell it, or use it to train
						general-purpose AI models. Its use and transfer of information
						received from Google APIs is subject to the{" "}
						<a href="https://developers.google.com/terms/api-services-user-data-policy">
							Google API Services User Data Policy
						</a>
						, including the Limited Use requirements.
					</p>
				</section>
				<section>
					<h2>Remote message content</h2>
					<p>
						Remote images and other externally loaded message resources are
						blocked by default. You can load them for a message or allow them
						for a sender or domain. If you allow them, the remote host may
						receive your IP address, request details, and information encoded in
						the sender’s resource URLs, including tracking identifiers.
					</p>
					<p>
						Opening a message link or attachment may involve your browser or
						another application. Their network activity and privacy practices
						are outside this website’s control.
					</p>
				</section>
				<section id="diagnostics">
					<h2>Diagnostics and optional telemetry</h2>
					<p>
						The app keeps local diagnostic logs, retaining up to 14 rolling log
						files by default. Logs are intended to record operations,
						identifiers, errors, and timings rather than message bodies,
						subjects, or credentials. Error messages and technical metadata can
						still contain identifying information; review logs before sharing
						them.
					</p>
					<p>
						Remote telemetry is off by default. A distributor or deployment
						operator can explicitly enable OpenTelemetry tracing and choose its
						receiving endpoint. When enabled, tracing may export request routes,
						HTTP destination metadata, status codes, timings, and error details.
						The operator of that endpoint is responsible for its handling and
						retention. Check the configuration and disclosures of the build you
						use.
					</p>
				</section>
				<section id="your-control">
					<h2>Your choices and deletion</h2>
					<p>
						You choose which accounts to add, how much mail to synchronise,
						whether to load remote content, and where to save exports. You can
						export messages as original .eml files and remove accounts from the
						application.
					</p>
					<p>
						Removing an account removes its local account records and stored
						credentials through the app’s cleanup process. It does not by itself
						delete your provider’s mailbox, calendar, or contacts. Sending,
						deleting, or changing provider data are separate actions; an
						operation already in progress may still complete remotely.
					</p>
					<p>
						Uninstalling the app does not necessarily remove its data directory,
						logs, backups, or exported files. Remove those separately if you no
						longer want them. Deleting database records is not a promise of
						secure erasure from storage devices or backups.
					</p>
				</section>
				<section id="contact">
					<h2>Questions or privacy requests</h2>
					<p>
						Contact the Mylo Mail project using the email below. If your
						question concerns an independently distributed build or a provider’s
						own data, contact that distributor or provider as well.
					</p>
					<ContactEmail />
					<p>
						If you email us, your address and message are used to handle the
						conversation. Please do not send passwords, access tokens, or
						private mailbox content. You can ask about access to or deletion of
						correspondence you have sent us, subject to any legal obligations.
					</p>
				</section>
				<section>
					<h2>Changes to this policy</h2>
					<p>
						As Mylo Mail develops, this page will be updated to reflect changes
						in the project website and standard application. The date above
						identifies the current version.
					</p>
				</section>
			</div>
		</main>
	);
}
