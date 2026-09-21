@echo off
title SAP Keep-Alive Launcher
cd /d "%~dp0"

set AHK="C:\Program Files\AutoHotkey\v2\AutoHotkey64.exe"

if exist %AHK% (
    start "" %AHK% "%~dp0SapKeepAlive.ahk"
    echo SAP Keep-Alive has been launched in the background!
) else (
    echo AutoHotkey v2 was not found in default path: %AHK%
    pause
)
