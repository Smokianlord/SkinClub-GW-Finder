$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repo = Split-Path $PSScriptRoot -Parent
$version = '1.3.0'
$tag = 'v' + $version
$output = Join-Path $repo ('dist/' + $tag)
$stage = Join-Path ([IO.Path]::GetTempPath()) ('SkinClub-release-' + [Guid]::NewGuid().ToString('N'))
$source = Join-Path $stage 'source'
$portable = Join-Path $stage 'portable'
New-Item -ItemType Directory -Force $output, $source, $portable | Out-Null
Add-Type -AssemblyName System.IO.Compression.FileSystem

try {
    # Take the actual working-tree files, including this release's uncommitted edits.
    # Do not include ignored executables, user data, logs, or the Git database.
    $files = @(& git -C $repo ls-files --cached --others --exclude-standard)
    if ($LASTEXITCODE -ne 0) { throw 'Cannot enumerate source files' }
    foreach ($relative in $files) {
        if ($relative -match '(^|/)(dist|\.git)/|\.(exe|dll|pdb|log|zip)$|(^|/)data\.json') { continue }
        $destination = Join-Path $source $relative
        New-Item -ItemType Directory -Force (Split-Path $destination -Parent) | Out-Null
        Copy-Item -LiteralPath (Join-Path $repo $relative) -Destination $destination -Force
    }
    $compiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
    if (!(Test-Path $compiler)) { $compiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework/v4.0.30319/csc.exe' }
    $exe = Join-Path $portable 'SkinClubGWFinder.exe'
    & $compiler /nologo /codepage:65001 /target:winexe /optimize+ "/out:$exe" "/win32icon:$source/SkinClubGWFinder.ico" /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:System.Net.Http.dll /reference:System.Web.Extensions.dll (Join-Path $source 'Program.cs') (Join-Path $source 'BrowserRuntime.cs')
    if ($LASTEXITCODE -ne 0) { throw 'Release compilation failed' }
    $metadata = [Diagnostics.FileVersionInfo]::GetVersionInfo($exe)
    if ($metadata.FileVersion -ne "$version.0" -or $metadata.ProductVersion -ne $version) { throw 'Executable version mismatch' }
    foreach ($name in @('README_FIRST.txt', 'README.md', 'CHANGELOG.md', 'VERSION.txt', 'SkinClubGWFinder.ico', 'run.bat')) {
        Copy-Item -LiteralPath (Join-Path $source $name) -Destination $portable
    }
    Copy-Item -LiteralPath (Join-Path $source "releases/$tag.md") -Destination (Join-Path $output 'RELEASE_NOTES.md') -Force
    Copy-Item -LiteralPath $exe -Destination (Join-Path $output 'SkinClubGWFinder.exe') -Force
    $assets = @('SkinClubGWFinder.exe', "SkinClub-GW-Finder-$tag-Windows.zip", "SkinClub-GW-Finder-$tag-Source.zip")
    foreach ($pair in @(@($portable, $assets[1]), @($source, $assets[2]))) {
        $archivePath = Join-Path $output $pair[1]
        if (Test-Path -LiteralPath $archivePath) { Remove-Item -LiteralPath $archivePath }
        [IO.Compression.ZipFile]::CreateFromDirectory($pair[0], $archivePath)
    }
    $sums = foreach ($asset in $assets) {
        $hash = (Get-FileHash -LiteralPath (Join-Path $output $asset) -Algorithm SHA256).Hash.ToLowerInvariant()
        "$hash  $asset"
    }
    $sums | Set-Content -LiteralPath (Join-Path $output 'SHA256SUMS.txt') -Encoding ascii
    @'
# Upload v1.3.0 to GitHub

Nothing has been published, pushed, or tagged by the packaging script.

1. Commit and push the release changes to your repository first. Include Program.cs,
   BrowserRuntime.cs, VERSION.txt, README_FIRST.txt, README.md, CHANGELOG.md,
   releases/v1.3.0.md, scripts/package-release.ps1, .gitignore, and any other pending
   source/build/test changes. Do not commit dist/ or the executable.
   If you use the source ZIP to update the repository, extract its CONTENTS into the
   repository root; do not upload the ZIP itself as a substitute for updating source.
2. Open https://github.com/Smokianlord/SkinClub-GW-Finder/releases/new
3. Create tag: v1.3.0. Target the commit containing the release changes from step 1.
4. Release title: SkinClub GW Finder v1.3.0
5. Paste the contents of RELEASE_NOTES.md into the description.
6. Attach these four files from this folder:
   - SkinClub-GW-Finder-v1.3.0-Windows.zip (recommended download)
   - SkinClubGWFinder.exe (standalone executable)
   - SkinClub-GW-Finder-v1.3.0-Source.zip (complete source snapshot)
   - SHA256SUMS.txt
7. Set as the latest release, leave pre-release unchecked, and publish when ready.

RELEASE_NOTES.md and this checklist are preparation files, not required assets.
The outer release-kit ZIP is for your convenience; upload the four assets inside,
not the outer ZIP. GitHub also generates source downloads from the selected tag.
This is why committing and pushing the matching source before creating the tag matters.
'@ | Set-Content -LiteralPath (Join-Path $output 'UPLOAD_TO_GITHUB.md') -Encoding utf8

    # Check archive contents, including dot-directories excluded by some ZIP tools.
    $archive = [IO.Compression.ZipFile]::OpenRead((Join-Path $output $assets[2]))
    try {
        $entries = @($archive.Entries | ForEach-Object { $_.FullName.Replace('\', '/') })
        foreach ($required in @('Program.cs', 'BrowserRuntime.cs', 'BUILD_EXE.bat', '.github/workflows/build.yml', 'tests/run.ps1', 'scripts/package-release.ps1', 'releases/v1.3.0.md')) {
            if ($entries -notcontains $required) { throw "Missing source archive entry: $required" }
        }
        if (@($entries | Where-Object { $_ -match '(^|/)data\.json|\.exe$|(^|/)\.git/' }).Count -gt 0) { throw 'Unexpected private or binary source entry' }
    } finally { $archive.Dispose() }
    $kit = Join-Path $repo "dist/SkinClub-GW-Finder-$tag-release-kit.zip"
    if (Test-Path -LiteralPath $kit) { Remove-Item -LiteralPath $kit }
    [IO.Compression.ZipFile]::CreateFromDirectory($output, $kit)
    Write-Output "Release assets: $output"
    Write-Output "Complete release kit: $kit"
    Get-ChildItem -LiteralPath $output | Select-Object Name, Length
} finally {
    $resolvedStage = [IO.Path]::GetFullPath($stage)
    $allowedPrefix = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\SkinClub-release-'
    if (!$resolvedStage.StartsWith($allowedPrefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unexpected staging path; cleanup refused' }
    if (Test-Path -LiteralPath $resolvedStage) { Remove-Item -LiteralPath $resolvedStage -Recurse -Force }
}
