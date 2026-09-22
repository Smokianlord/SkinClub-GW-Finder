SkinClub GW Finder v1.3.0

RUN
Extract the Windows ZIP, close older copies, and run SkinClubGWFinder.exe.
No installer required. Requires Windows 10/11, .NET Framework 4.8, internet,
and Edge or Chrome for JavaScript-rendered page details. Build is unsigned.

UPGRADE AND SAVED DATA
Your data remains in %LOCALAPPDATA%\SkinClub GW Finder\data.json.
Back it up before upgrading if you want to retain all older History entries.
This version keeps only the 30 most recent History giveaways and automatically
removes the rest. Joined giveaways are preserved. Saves keep a data.json.bak
backup, and unreadable data files are preserved separately.

WHAT CHANGED
- Five app-owned browser processes maximum, including children; one rendered
  page at a time. Each process group closes after its work finishes.
- Live Logs tab with browser counts, queued pages, results, Copy and Clear.
- Dedicated giveaway tabs, rounded controls, and smoother filtering.
- Safer saves, redirect handling, and row actions.

BUILD FROM SOURCE
Extract the source ZIP and run BUILD_EXE.bat on Windows. It compiles Program.cs
and BrowserRuntime.cs. Historical *_FIX.txt files describe earlier releases;
use README.md and this file for current behavior.

Project: https://github.com/Smokianlord/SkinClub-GW-Finder
Unofficial project; not affiliated with or endorsed by SkinClub.
