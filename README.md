# SkinClub GW Finder

**SkinClub GW Finder** is a Windows desktop app for discovering, validating, tracking, and organizing direct SkinClub creator giveaway links.

It focuses on actual dated giveaway landing pages rather than creator homepages or generic referral links.

> **Unofficial project:** This app is not affiliated with, endorsed by, or sponsored by SkinClub or any creator/partner it discovers.

## Features

- **Deep Search** across public sources, including YouTube, Telegram, web search, and known creator `.club` domains.
- **Direct-page validation** before a giveaway is added to the tracker.
- **Active / Joined / History workflow** for keeping giveaways organized.
- **Joined exclusivity:** joining a giveaway removes it from Active or History and shows it only in Joined. Removing it from Joined restores it to its real Active/History state.
- **Ticket tracking** with `remaining / total` values when available.
- **Deadline parsing** with clean uppercase calendar dates.
- **Sorting** by creator, URL, tickets remaining, and deadline.
- **Filtering/search** by creator name, all fields, URL, ticket count, or deadline.
- **Copy and open actions** for direct giveaway links.
- **Automatic refresh** every 10 minutes, plus manual Refresh.
- **Persistent local data** between launches.
- **Migration support** for data created by older builds.
- **Compact status UI** with a polished dark desktop design and view-specific accents.

## How it works

Deep Search gathers candidate giveaway URLs from public sources and known creator domains, then opens the actual giveaway page to validate it. The app attempts to extract:

- creator name
- direct giveaway URL
- ticket count
- active/ended state
- deadline or countdown-derived end date

Network failures do **not** automatically mark a giveaway as ended.

## Requirements

- Windows 10 or Windows 11
- .NET Framework 4.8 enabled
- Internet connection for Refresh and Deep Search

No account login or API key is required.

## Quick start

1. Download or clone the repository.
2. Double-click `run.bat`.
3. The script compiles `Program.cs` into `SkinClubGWFinder.exe` using the .NET Framework C# compiler included with Windows.
4. The app launches automatically after a successful build.

## Build manually

The included `run.bat` uses the .NET Framework compiler from one of these locations:

```text
%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
```

The generated executable is intentionally excluded from Git through `.gitignore`.

## Data storage

User data is stored locally at:

```text
%LOCALAPPDATA%\SkinClub GW Finder\data.json
```

The app also attempts to migrate compatible data from older builds automatically.

## Views

### Active
Shows currently active giveaways that have not been marked as Joined.

### Joined
Shows giveaways you have explicitly joined. Joined items are hidden from both Active and History until removed from Joined.

### History
Shows ended giveaways that have not been marked as Joined.

## Search and sorting

Use the search field to filter by:

- Creator name
- All fields
- URL
- Ticket
- Deadline

`Ctrl + F` focuses the search box. Clicking sortable table headers toggles ascending/descending order.

## Known limitations

SkinClub creator pages and public search/source markup can change without notice. If a source changes its HTML structure, discovery or parsing may require an update.

Deep Search depends on third-party public pages being reachable from your network.

## Privacy

The app stores its tracker data locally on your PC. It does not require SkinClub credentials or creator-account credentials.

## Contributing

Bug reports and feature suggestions are welcome. See [CONTRIBUTING.md](CONTRIBUTING.md) before opening a pull request.

## License

No open-source license is included yet. Unless a license is added by the repository owner, normal copyright rules apply.
