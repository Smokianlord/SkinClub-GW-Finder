$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$compiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
if (!(Test-Path $compiler)) { $compiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework/v4.0.30319/csc.exe' }
$output = Join-Path $env:TEMP ('SkinClub-live-tests-' + [Guid]::NewGuid().ToString('N') + '.exe')
& $compiler /nologo /target:exe /main:LivePageTests "/out:$output" /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:System.Net.Http.dll /reference:System.Web.Extensions.dll (Join-Path $repo 'Program.cs') (Join-Path $repo 'BrowserRuntime.cs') (Join-Path $PSScriptRoot 'LivePageTests.cs')
if ($LASTEXITCODE -ne 0) { throw 'Live test compilation failed' }
& $output
if ($LASTEXITCODE -ne 0) { throw 'Live page tests failed' }
