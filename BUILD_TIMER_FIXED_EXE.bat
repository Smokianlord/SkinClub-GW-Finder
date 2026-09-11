@echo off
setlocal
cd /d "%~dp0"
title Build SkinClub GW Finder

set "APP=SkinClubGWFinder.exe"
set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"

if not exist "%CSC%" (
  echo.
  echo ERROR: Windows .NET Framework compiler was not found.
  echo Enable/install .NET Framework 4.8 and run this again.
  echo.
  pause
  exit /b 1
)

for %%F in ("SkinClubGWFinder.exe" "SkinClubGWFinder_NEW.exe" "SkinClubGWFinder_TIMER_FIXED.exe") do (
  if exist %%F del /f /q %%F
)

echo Building SkinClub GW Finder into %APP% ...
"%CSC%" /nologo /codepage:65001 /target:winexe /optimize+ /win32icon:"SkinClubGWFinder.ico" /out:"%APP%" ^
  /reference:System.dll ^
  /reference:System.Core.dll ^
  /reference:System.Drawing.dll ^
  /reference:System.Windows.Forms.dll ^
  /reference:System.Net.Http.dll ^
  /reference:System.Web.Extensions.dll ^
  Program.cs

if errorlevel 1 (
  echo.
  echo BUILD FAILED. No EXE is included in this ZIP, so there is no stale EXE to launch.
  echo.
  pause
  exit /b 1
)

echo.
echo SUCCESS: %APP%
echo The title bar is: SkinClub GW Finder
echo.
start "" "%APP%"
exit /b 0
