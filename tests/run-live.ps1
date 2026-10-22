$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$output = Join-Path $env:TEMP ('SkinClub-live-tests-' + [Guid]::NewGuid().ToString('N') + '.exe')
& (Join-Path $repo 'scripts/build.ps1') -Output $output -Target exe -Main LivePageTests -ExtraSource (Join-Path $PSScriptRoot 'LivePageTests.cs')
& $output
if ($LASTEXITCODE -ne 0) { throw 'Live page tests failed' }
