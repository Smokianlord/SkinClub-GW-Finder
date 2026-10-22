@echo off
setlocal
cd /d "%~dp0"
title Build SkinClub GW Finder

set "APP=SkinClubGWFinder.exe"
if exist "%APP%" del /f /q "%APP%"

echo Building SkinClub GW Finder...
powershell -NoProfile -ExecutionPolicy Bypass -File "scripts\build.ps1" -Output "%CD%\%APP%"

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
