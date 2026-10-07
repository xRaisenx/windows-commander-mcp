@echo off
setlocal
title Windows Commander Rescue - Live Control Center
mode con cols=150 lines=34 >nul 2>&1

powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0Start-NMBV-WindowsCommander.ps1" -RuntimeConfigPath "%~dp0runtime.json" -TakeManagedOwnership
if errorlevel 1 (
  echo.
  echo Windows Commander startup failed.
  exit /b 1
)

call "%~dp0Watch-WindowsCommander.cmd"
