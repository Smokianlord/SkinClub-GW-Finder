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

if exist "%APP%" del /f /q "%APP%"

echo Building SkinClub GW Finder...
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
  echo BUILD FAILED.
  echo.
  pause
  exit /b 1
)

echo.
echo SUCCESS: %APP%
echo.
start "" "%APP%"
exit /b 0
