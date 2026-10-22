$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$output = Join-Path $env:TEMP ('SkinClub-browser-tests-' + [Guid]::NewGuid().ToString('N') + '.exe')
& (Join-Path $repo 'scripts/build.ps1') -Output $output -Target exe -Main BrowserLifecycleTests -ExtraSource (Join-Path $PSScriptRoot 'BrowserLifecycleTests.cs')
& $output
if ($LASTEXITCODE -ne 0) { throw 'Browser lifecycle tests failed' }
