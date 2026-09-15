# Current handoff

## Completed work

- Reworked the renderer into a modern Outlook-style desktop hierarchy without changing the frozen shell architecture: branded app bar, global mail search, persistent application rail, contextual mail command bar, and the existing per-window resizable folder/list/reading workspace.
- Redesigned the folder pane around account sections and compact two-line mailbox rows while retaining persistent collapse/reorder behavior, provider counts, availability warnings, drag/drop, and keyboard/context-menu paths.
- Redesigned the message list as a dense Outlook-style stacked list with a mailbox heading, compact filters, visible sortable controls, locale-aware dates, unread/selected treatment, and Carbon attachment/flag/failure icons.
- Restyled the reading pane, compose view, drafts, account settings, app settings, add-account form, calendar, and contacts into one consistent Carbon-token surface. Contacts now has a proper page header, search region, contact cards, status chips, and editor panel.
- Added `@carbon/icons-react` as a direct renderer dependency and removed emoji-based warning/attachment affordances from the touched surfaces.
- Preserved the mail command bar while app settings is open because the detached-compose/tray workflow relies on New message remaining reachable there.

## Verification

- Final `pnpm check`: format, TypeScript, ESLint, Stylelint, build, 697 .NET tests, and 226 Vitest tests passed.
- Actual Electron visual smoke passed at 1440×900 for Mail, Compose, Account settings, Add account, Settings, People, and Calendar; screenshots were inspected and the throwaway smoke spec was removed afterward.
- Actual Electron minimum-window layout test passed at the enforced 720×480 minimum with no document overflow and all three resizable panels usable.
- Actual Electron MailFlow, Compose, Calendar empty/editor/invite, and Drafts server-save flows passed. MailFlow covered the sortable/filterable list and keyboard actions against a real IMAP server.
- Actual Electron CalDAV CRUD and detached-compose multi-window/tray restore regressions passed after the UI command-bar visibility correction.
- `pnpm check:deep` was not run because repository policy reserves it for explicit requests. This change did not touch persistence, provider, sync, mutation, or reconciliation invariants.

## External verification still required

- **Gmail expired history:** provide `GMAIL_EXPIRED_HISTORY_ID` from an account with a genuinely expired history cursor. Gmail exposes no API to create one; cursor `1` is not a deterministic substitute.
- **Native Gmail and Graph RSVP:** configure attendee-owned invitations from second consenting Gmail and Microsoft 365 calendar accounts. Ordinary calendar CRUD/recurrence does not prove attendee response actions.

## Next task

- UI rework is complete and green. Continue only from concrete product feedback or a named interaction/accessibility issue; do not restructure the frozen per-window shell/store/query boundaries.

## Required reading

- `AGENTS.md`
- `docs/architecture.md` §12 and §13
- `docs/skills/frontend-shell.md`
- The relevant guide under `docs/skills/` before changing persistence, providers, sync, mutations, fault injection, or the frontend shell.

## Live risks / decisions

- The visual target is modern Outlook's hierarchy and density, implemented with Carbon components/tokens rather than a pixel copy or Microsoft-specific controls.
- Search remains mailbox/account scoped even though it now occupies the global app-bar position; no query or backend semantics changed.
- Panel layout remains global-default state with per-window live independence. Existing windows keep their own layout; only a later window inherits the latest completed resize.
- Gmail expired-history and native RSVP coverage remain conditional on external fixtures rather than nondeterministic substitutes.
