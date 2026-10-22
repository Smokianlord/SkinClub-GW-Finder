# Single source of truth for compiling the app and its tests.
# Usage:
#   build.ps1 [-Output <exe>] [-Target winexe|exe] [-Main <class>] [-ExtraSource <file>...]
param(
    [string]$Output = (Join-Path (Split-Path $PSScriptRoot -Parent) 'SkinClubGWFinder.exe'),
    [string]$Target = 'winexe',
    [string]$Main = '',
    [string[]]$ExtraSource = @()
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (!(Test-Path $compiler)) { $compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
if (!(Test-Path $compiler)) { throw 'The .NET Framework C# compiler was not found. Enable .NET Framework 4.8.' }

$sources = @(Get-ChildItem (Join-Path $repo 'src') -Filter *.cs -Recurse | ForEach-Object { $_.FullName }) + $ExtraSource
$arguments = @(
    '/nologo', '/codepage:65001', "/target:$Target", '/optimize+',
    "/win32icon:$(Join-Path $repo 'SkinClubGWFinder.ico')", "/out:$Output",
    '/reference:System.dll', '/reference:System.Core.dll', '/reference:System.Drawing.dll',
    '/reference:System.Windows.Forms.dll', '/reference:System.Net.Http.dll', '/reference:System.Web.Extensions.dll'
)
if ($Main) { $arguments += "/main:$Main" }
& $compiler @arguments @sources
if ($LASTEXITCODE -ne 0) { throw "Build failed (csc exit code $LASTEXITCODE)" }
Write-Host "Built $Output"
