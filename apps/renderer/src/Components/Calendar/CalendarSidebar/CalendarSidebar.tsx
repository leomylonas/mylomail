import { useState } from "react";
import { Checkbox } from "@carbon/react";
import styles from "@mylomail/renderer/Components/Calendar/CalendarSidebar/CalendarSidebar.module.css";

export interface CalendarSidebarCalendar {
	id: string;
	name: string;
	/** The resolved `#rrggbb` the calendar is drawn in everywhere else. */
	colour: string;
	visible: boolean;
}

export interface CalendarSidebarAccount {
	id: string;
	displayName: string;
	emailAddress?: string;
	color: string;
	/** False until the account's calendar list has arrived, so "none" is not claimed early. */
	loaded: boolean;
	calendars: CalendarSidebarCalendar[];
}

/**
 * Every account's calendars, each with a checkbox for the unified view (§13 Epic 7). It
 * follows the mail `Sidebar` / `MailboxTree` shape: a collapsible heading per account, a plain
 * nested list under it, and rows that are buttons rather than an ARIA tree widget.
 *
 * A calendar row has two separate controls so neither can be mistaken for the other: the
 * checkbox toggles visibility (Space), the name button opens that calendar in its own window
 * (double-click, or Enter when focused). Collapsing an account here is this window's own
 * state; unlike the mail sidebar it is not persisted.
 */
export function CalendarSidebar({
	accounts,
	onToggleCalendar,
	onOpenCalendar,
}: {
	accounts: CalendarSidebarAccount[];
	onToggleCalendar: (calendarId: string, visible: boolean) => void;
	onOpenCalendar: (calendarId: string, accountId: string) => void;
}) {
	const [collapsed, setCollapsed] = useState<ReadonlySet<string>>(
		() => new Set(),
	);

	const toggleCollapsed = (accountId: string) =>
		setCollapsed((current) => {
			const next = new Set(current);
			if (!next.delete(accountId)) next.add(accountId);
			return next;
		});

	return (
		<nav className={styles.sidebar} aria-label="Accounts and calendars">
			{accounts.map((account) => {
				const isCollapsed = collapsed.has(account.id);
				const accountName = account.displayName || account.emailAddress || "";
				return (
					<section key={account.id} className={styles.section}>
						<button
							type="button"
							className={styles.header}
							aria-expanded={!isCollapsed}
							onClick={() => toggleCollapsed(account.id)}
						>
							<span className={styles.chevron} aria-hidden>
								{isCollapsed ? "▸" : "▾"}
							</span>
							<span
								className={styles.accountSwatch}
								style={{
									backgroundColor: account.color || "var(--cds-icon-secondary)",
								}}
								aria-hidden
							/>
							<span
								className={styles.name}
								title={
									account.emailAddress && account.emailAddress !== accountName
										? `${accountName} (${account.emailAddress})`
										: accountName
								}
							>
								{accountName}
							</span>
						</button>
						{isCollapsed ? null : (
							<div className={styles.tree}>
								{account.calendars.length > 0 ? (
									<ul aria-label={`${accountName} calendars`}>
										{account.calendars.map((calendar) => {
											const name = calendar.name || "Untitled calendar";
											return (
												<li key={calendar.id}>
													<div className={styles.row}>
														<div className={styles.check}>
															<Checkbox
																id={`calendar-visible-${calendar.id}`}
																labelText={name}
																hideLabel
																checked={calendar.visible}
																onChange={(_, { checked }) =>
																	onToggleCalendar(calendar.id, checked)
																}
															/>
														</div>
														<button
															type="button"
															className={styles.item}
															title={`${name} — double-click or press Enter to open it in its own window`}
															onDoubleClick={() =>
																onOpenCalendar(calendar.id, account.id)
															}
															onKeyDown={(event) => {
																if (event.key !== "Enter") return;
																// Enter would otherwise only synthesise a click, which does
																// nothing here; it is the keyboard form of a double-click.
																event.preventDefault();
																onOpenCalendar(calendar.id, account.id);
															}}
														>
															<span
																className={styles.swatch}
																style={{ backgroundColor: calendar.colour }}
																aria-hidden
															/>
															<span className={styles.name}>{name}</span>
														</button>
													</div>
												</li>
											);
										})}
									</ul>
								) : (
									<p className={styles.empty}>
										{account.loaded ? "No calendars" : "Loading…"}
									</p>
								)}
							</div>
						)}
					</section>
				);
			})}
		</nav>
	);
}
