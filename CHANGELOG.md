# v1.2.2

- Added the app version to the window title and main header.
- Added v1.2.2 EXE version metadata.
- Includes all fixes from the current sorting/deep-search build.

# Changelog

All notable changes to SkinClub GW Finder will be documented in this file.

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
