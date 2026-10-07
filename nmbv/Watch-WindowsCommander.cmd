@echo off
setlocal
title Windows Commander Rescue - Live Control Center
mode con cols=150 lines=46 >nul 2>&1
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0Watch-WindowsCommander.ps1"
