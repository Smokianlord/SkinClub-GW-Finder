SkinClub GW Finder - corrected source

1. Extract this folder.
2. Double-click BUILD_EXE.bat.
3. The script compiles SkinClubGWFinder.exe from Program.cs and launches it.

Corrections in this build:
- Giveaway URLs are canonicalized to https://creator.club/DDMMYY/ (tracking/query parameters are discarded).
- DrewCS2 discovery remains supported through drewcs2.club; no special query string is required.
- History cleanup is no longer based on the date embedded in the giveaway URL.
- An ended giveaway stays in History for one full calendar month from when it enters History.
- Existing History from older versions is NOT wiped on first launch; it receives a fresh retention timestamp.
- Joined entries are never removed by History cleanup.
