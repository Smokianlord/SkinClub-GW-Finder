# Changelog

## [2.3.1] - 2026-11-13

- Fixed: closed quizzes were listed alphabetically, so a quiz that had just ended could land anywhere. Each quiz's real end time is now saved (closed quizzes read it once from their own page), and the Quizzes tab lists live quizzes first, then closed ones from the most recently ended to the oldest. Closed quizzes show their end date and "ended today", "ended yesterday" or "N days ago".

## [2.3.0] - 2026-11-12

- New **Quizzes** tab: the SkinClub community quizzes (community.skin.club/en/quizzes) with link, 1st-place prize (live quizzes), participants and end date; live quizzes first. The list is updated by Refresh, Deep Search and the automatic checks.
- The **Active** tab is now called **Creator**.
- Fixed: giveaways whose entry code carries a different date than their link (for example `joispoi.club/021126` with `LP-JOISPOI24-011126`, `theasylum.club/011126` with `LP-ASYLUM-011226`) were saved without promocode and deposit. Any dated entry code is now accepted; the undated discount code is still ignored.

## [2.2.0] - 2026-11-10

- Closing the window now keeps the app running in the system tray, still checking for giveaways. The tray icon's menu has Open, Deep Search, Refresh, Joined giveaways and Exit; a search started from the tray reports its result in a tray pop-up. Exit really closes the app.
- The tray icon is always shown; the Notifications setting only controls the pop-ups about new giveaways.
- Windows, the taskbar, Alt+Tab and the tray use the full multi-size app icon, so it stays sharp at every size. The app has its own taskbar identity, so the taskbar shows the current icon instead of an old one Windows cached for the exe.
- Tray menu: icons, text and separator lines share one left edge, and icons are centred on their row.

## [2.1.0] - 2026-11-10

- New **Prize Pool** column: each giveaway's total prize value in USD (for example `1,000$`), read from the page in any language. It sorts by amount and can be searched. Saved giveaways, including ended ones, get their prize pool filled in on the next Refresh or Deep Search.
- The window's minimum width is 1290 px so every column, names, links and codes stay fully visible.

## [2.0.0] - 2026-11-08

- New look in the SkinClub style: deep indigo background with pink and electric-blue glows and faint 3D gifts, coins, cash, gems and knives behind the whole window (including the table); angular cut-corner buttons with the pink-to-red primary gradient; a stats strip (active giveaways and tickets left, ending within 48 hours, joined, cheapest entry); creator tiles, neon ticket bars, promo chips, gold coin deposits and "in N days" under each deadline; restyled Settings and Add links dialogs.
- 3D icons, embedded in the exe. New app icon and header: a 3D gift on a dark tile, "SkinClub GW Finder" with the version beside it.
- Text uses Segoe UI and Consolas, which Windows draws sharply (variable fonts such as Bahnschrift come out with uneven edges).
- Smoother window: resizing by dragging an edge no longer stutters (layout waits while the edge moves and catches up when the mouse rests or is released; about 9 ms per mouse move instead of about 180 ms), the background art is drawn once at screen size and re-laid out off the UI thread after a resize, and buttons, tiles and panels keep their finished picture instead of redrawing gradients and icons on every repaint (a full repaint dropped from about 48 ms to 18 ms; maximising from about 160 ms to 55 ms).
- Columns are sized so creator names, links and entry codes are shown in full, down to the smallest window size; long values step down a font size instead of being cut off.

- Fixed: giveaway pages in Spanish, Portuguese, Turkish and Arabic were often saved as "unknown", got the discount code instead of the entry code, or had no minimum deposit. The end date is now read from the page's own data (`data-giveaway-complete-date`), which every page carries in every language, so no browser is needed for it; ticket labels in more languages are recognised, plus any "label: 12 / 50" line in other languages; the entry code is the one ending with the giveaway's date.
- 15 more creators are built in (Peereira, Neok, Lekoen, Lukinhas, Garmour, Pinkdrive, BrunoCS2, TACO, Paulinhoeletric, Vitokng, Wtcn, Imorr, Skinwin, Maherco, Elfanta). Every creator domain that is saved is probed as well, and Deep Search also probes next month's dates.
- Refresh is now a quick check: tickets, deadline and ended status of the saved giveaways, without opening a browser.
- Add links: paste many giveaway links at once, one per line.
- Minimum deposits are shown in one USD form, for example `5$` (no more `US$5` / `$5` mix). Saved values are converted on start-up.
- The browser is chosen automatically (Edge first, then Chrome, Brave, Chromium); the browser field, "Open settings.json" and the `BrowserPath` / `ExtraCreators` options were removed from Settings.

- Fixed: automatic refreshes only re-checked giveaways that were already saved, so new giveaways were found only when you pressed Deep Search and the "new giveaway" notification almost never appeared. The app now also runs a Deep Search automatically every 60 minutes (set in Settings; minimum 15, 0 turns it off). Start-up itself only refreshes the saved giveaways; the first automatic Deep Search comes on a later timer tick. An automatic Deep Search uses at most two browsers, shows no error pop-ups, waits a full interval before retrying after a failure, and sends the tray notification for new finds.
- Settings: the refresh field is now labelled "Recheck saved giveaways every", next to the new "Search for new giveaways every" field.

## [1.5.3] - 2026-10-30

- Fixed: a giveaway's deadline was never updated once it had been saved, so an extended giveaway kept its old date. The live timer is now read again whenever you press Refresh or Deep Search, and on automatic refreshes when the saved deadline is older than 30 minutes (`DeadlineRecheckMinutes` in `settings.json`). Automatic re-checks use at most two browsers at a time.

## [1.5.2] - 2026-10-27

- History now opens with the most recently ended giveaway on top (newest ended first), the sort arrow shows on the Deadline column, and clicking that header reverses the order. Each tab (Active, Joined, History) remembers its own sort.

## [1.5.1] - 2026-10-23

- Fixed: Portuguese giveaway pages show "FIM" for "ended"; the app did not know the word and a refresh reopened ended giveaways. "FIM" and the "ended" word in several other languages are now recognised, and an ended giveaway is only reopened by a real future deadline.
- Fixed: a Join click (or an added link) made while a scan was running was overwritten when the scan finished. Scan results are now merged into the live data.
- Fixed: the Settings dialog note overlapped the buttons; dialogs now have a layout test.
- Logs tab: shows the scan phase, sources read, page progress and running browsers instead of a bare process count.
- Security and robustness: a typed link must be one giveaway link and nothing else, only web links are opened, TLS 1.3 is allowed where Windows supports it, and dead code was removed.
- Faster and lighter: browser pages use one DevTools connection instead of about 40, the heavy whole-page scan only runs while a promocode or deposit is still missing, and Deep Search no longer re-downloads giveaways already saved as ended. The status message no longer keeps the buttons disabled for several seconds after a scan.

## [1.5.0] - 2026-10-23

- Only one copy of the app can run; starting another brings the first to the front.
- Unhandled errors are logged to `crash.log`; empty `catch` blocks now log to the Logs tab.
- New Settings dialog and `settings.json` (refresh interval, History size, notifications, browser, timeouts, `ExtraCreators`).
- Optional tray notifications for new giveaways and giveaways ending today.
- Faster, lighter Deep Search: headers-first requests, capped page sizes, page checks overlap with discovery, higher probe concurrency, scan data copied without a JSON round-trip, memory trimmed after scans.
- Fixed: History showed an unrelated code (for example `LP-XTVFAGX7QP9W`) instead of the giveaway's entry code, because ended pages only show that other code. A code is now accepted only if it ends with the URL's DDMMYY slug, ended pages never overwrite saved codes or deposits, and existing damaged entries are repaired on load (a one-time copy is kept as `data.json.before-code-repair`).
- History now shows each creator's latest giveaway (newest dated link) and sorts a creator's rows newest first; older entries stay saved. A creator's latest ended giveaway is never pruned by the History limit. Turn this off in Settings.
- Logs tab: shows live page-check progress (running / done of total) next to browser processes, refreshes twice a second, and no longer freezes the window while browser cleanup is waiting.
- Limits raised: up to 6 pages render in parallel (each browser in its own Windows job with a process limit and a share of a 4 GB memory budget), 64 concurrent page checks, 128 connections. All configurable in `settings.json`. Deep Search from scratch dropped from about 330 s to about 15 s with identical results.
- `Program.cs` split into `src/` files; `scripts/build.ps1` is the single build entry point; CI now runs the UI and browser-lifecycle tests.

## [1.4.0] - 2026-10-04

- Much faster refresh: metadata already resolved for a saved giveaway (deadline, promocode, minimum deposit) is reused instead of relaunching a headless browser for every page on every refresh. Pages that still lack metadata are retried at most every 12 hours.
- Complete interface redesign: flat dark theme, underline tabs with count badges, rounded search field with a dark field picker, 56px rows with creator avatars, ticket progress bars, promocode chips, deadline countdown, hover states, scan progress strip, status bar and dark title bar.
- 3D buttons everywhere, interactive per-view coloured tabs, Logs moved to the bottom-left corner, deadline shows the date only, more padding and no clipped text.
- Fixed: the wrong (discount / special bonus) promocode could be shown; the STEP 2 entry code is now used.
- Fixed: crash (disposed ContextMenuStrip) when choosing a search filter.
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

