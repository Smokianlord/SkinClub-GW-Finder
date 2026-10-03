# Changelog

## [Unreleased]

- Much faster refresh: metadata already resolved for a saved giveaway (deadline, promocode, minimum deposit) is reused instead of relaunching a headless browser for every page on every refresh. Pages that still lack metadata are retried at most every 12 hours.
- Complete interface redesign: flat dark theme, underline tabs with count badges, rounded search field with a dark field picker, 56px rows with creator avatars, ticket progress bars, promocode chips, deadline countdown, hover states, scan progress strip, status bar and dark title bar.
- Faster HTTP checks (16 concurrent page checks during refresh).
- Faster browsing and searching: rows are built in one batch, searching only rebuilds the table rows, and sort comparisons use precompiled patterns.
- Log view updates once per second instead of twice.
- Repository cleanup: removed loose per-fix notes (history lives in this changelog and git).

## [1.3.0] - 2026-09-22

- Added a live Logs tab with process counts, queued pages, errors, Copy/Clear, and a bounded 2,000-event session history.
- Added dedicated Active, Joined, and History navigation with counts, rounded controls, dark search, and clearer table headers.
- Render one page at a time with a Windows-enforced cap of five app-owned browser processes, including children.
- Contain browser descendants before launch and terminate the complete group after each page, even if the root exits first.
- Added render timeout, cleanup watchdog, shutdown cancellation, and temporary-profile removal retries.
- Keep the 30 most recent History giveaways instead of applying a one-month retention rule. Joined entries are preserved.
- Improved filtering performance and preserved row selection.
- Added atomic saves with backups, corrupt-file preservation, and invalid saved-row handling.
- Fixed redirect identity handling and accidental right-click actions.
- Added UI, persistence, lifecycle, and real-link validation scripts.

## [1.2.4] - 2026-09-18

- Isolated Chromium/CDP sessions to prevent metadata mixing between creators.
- Validated rendered-page hostnames and repaired mismatched saved metadata.

## Earlier release notes

### v1.2.2

- Added the app version to the window title and main header.
- Added v1.2.2 EXE version metadata.
- Includes all fixes from the current sorting/deep-search build.

## [1.0.0] - 2026-09-08

### Added
- Active giveaway dashboard with direct-link Copy/Open actions.
- History view for ended giveaways.
- Joined view with exclusive Active/History behavior.
- Remove-from-Joined behavior that restores the giveaway to its real status view.
- Creator/all-fields/URL/ticket/deadline filtering.
- Numeric ticket sorting and chronological deadline sorting.
- Deep Search across YouTube, Telegram, web search, and creator partner domains.
- Direct giveaway-page validation.
- Automatic 10-minute refresh and manual Refresh.
- Persistent local tracker data with legacy migration.
- Uppercase date presentation and 12-hour AM/PM Last Check display.
- View-specific polished UI accents and dimensional action buttons.

### Fixed
- Giveaway header helper-text clipping at Windows DPI scaling.
- Joined giveaways incorrectly remaining visible in Active/History.
- Joined removal routing.
- Date capitalization consistency.

## Focus and Deep Search performance fix
- Prevented animated copy confirmation from taking window focus.
- Reduced Deep Search brute-force probing and parallelized discovery requests.
- Prevented unnecessary Chromium/CDP rendering for guessed non-giveaway URLs.

## v1.2.2 resource hotfix
- Fixed excessive CPU/RAM usage during Deep Search caused by concurrent headless Chromium fallback processes.
- Serialized rendered-page checks, lowered scan concurrency, added short-lived render caching, and ensured spawned browser process trees are cleaned up.
- Giveaway discovery coverage is unchanged.
