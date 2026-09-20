$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$compiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
if (!(Test-Path $compiler)) { $compiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework/v4.0.30319/csc.exe' }
$output = Join-Path $env:TEMP ('SkinClub-regression-' + [Guid]::NewGuid().ToString('N') + '.exe')
& $compiler /nologo /target:exe /main:RegressionTests "/out:$output" "/win32icon:$repo/SkinClubGWFinder.ico" /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:System.Net.Http.dll /reference:System.Web.Extensions.dll (Join-Path $repo 'Program.cs') (Join-Path $repo 'BrowserRuntime.cs') (Join-Path $PSScriptRoot 'RegressionTests.cs')
if ($LASTEXITCODE -ne 0) { throw 'Test compilation failed' }
& $output
if ($LASTEXITCODE -ne 0) { throw 'Regression tests failed' }
