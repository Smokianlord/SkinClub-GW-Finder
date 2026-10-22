$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$output = Join-Path $env:TEMP ('SkinClub-regression-' + [Guid]::NewGuid().ToString('N') + '.exe')
& (Join-Path $repo 'scripts/build.ps1') -Output $output -Target exe -Main RegressionTests -ExtraSource (Join-Path $PSScriptRoot 'RegressionTests.cs')
& $output
if ($LASTEXITCODE -ne 0) { throw 'Regression tests failed' }
