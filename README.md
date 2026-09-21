# SapKeepAlive

Lightweight, multi-session **SAP GUI Keep-Alive** utility for Windows. Runs silently in the taskbar notification area, keeping multiple SAP sessions alive without focus theft or lag.

## Features
- **Standalone Binary**: Native Windows executable (~25 KB). Zero installations, no Python/AHK required.
- **Automatic Continuous Discovery**: Automatically detects SAP sessions (`DP1`, `WP1`, etc.) as soon as they open.
- **Background Non-Blocking Keystrokes**: Asynchronous `PostMessage` (`WM_KEYDOWN`/`WM_KEYUP`) prevents UI freezes and never interrupts your typing.
- **Daily Auto-Reset**: Daily countdown timer automatically resets each morning or on restart.
- **Auto-Close / Logout Scheduler**: Gracefully closes/logs out all active SAP sessions at a designated time (e.g. 7:00 PM).
- **Auto-Start with Windows**: Toggleable startup registry integration.
- **GitHub Update Checks**: Checks for updates directly from GitHub releases.
- **Customizable**: Configurable ping interval, expire timer, and keep-alive key (`Shift`, `F8`, `F13`).

## Usage
1. Download `SapKeepAlive.exe` from [Releases](https://github.com/Hackers-lab/SapKeepAlive/releases).
2. Run `SapKeepAlive.exe`.
3. Access controls from the taskbar tray (hidden icons).

## Disclaimer
*This tool is provided for developer convenience to prevent inactivity logouts. Use at your own risk. Always ensure open transactions are saved before leaving your workstation or before scheduled auto-close times. The authors accept no liability for unsaved work or security compliance.*
