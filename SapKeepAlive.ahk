#Requires AutoHotkey v2.0
#SingleInstance Force
Persistent

; ============================================================
; SAP GUI MULTI-SESSION KEEP-ALIVE PRO
;
; Features:
;   - ZERO KEYBOARD HOTKEYS ASSIGNED (User controls everything via Tray)
;   - Visual Settings Window:
;       * Customizable Keep-Alive interval (1 - 60 minutes)
;       * Customizable Auto-Expire / Stop timer (e.g. stop after 8 hours)
;       * Selectable Keep-Alive key: Shift (Silent Safe) vs F8 (Test) vs F13
;   - Live Alive Stopwatch & Session Tracker (DP1, WP1, multiple users)
;   - Total Pulse Counter
;   - Asynchronous non-blocking PostMessage (0% lag, never hangs)
; ============================================================

SetWinDelay(-1)
SetControlDelay(-1)

; ------------------------------------------------------------
; SETTINGS & STATE
; ------------------------------------------------------------
global IntervalMinutes  := 5         ; Interval between pulses (minutes)
global ExpireHours      := 8         ; Auto-stop timer (0 = Never expire)
global SelectedKeyIndex := 1         ; 1 = Shift (Safe), 2 = F8 (Test), 3 = F13 (Ghost)
global KeyVKMap         := [0x10, 0x77, 0x7C] ; VK_SHIFT, VK_F8, VK_F13
global KeyNameMap       := ["Shift (Silent Safe)", "F8 (Test Action)", "F13 (Hidden Ghost)"]

global IsRunning        := true
global TotalPulsesSent  := 0
global ScriptStartTime  := A_Now
global ExpireEndTime    := ""        ; Calculated expiration timestamp
global ActiveSessionsMap := Map()    ; HWND -> {System, Title, FirstSeen, LastPingTime, PingCount}

; Calculate expiration timestamp
ResetExpireTimer()

; Start keep-alive timer
SetTimer(DispatchKeepAlive, IntervalMinutes * 60 * 1000)

; Check expiration every 30 seconds
SetTimer(CheckExpiration, 30000)

; Update tray tooltip every 30 seconds
SetTimer(UpdateTrayTipStats, 30000)
UpdateTrayTipStats()

; ------------------------------------------------------------
; CORE KEEP-ALIVE DISPATCH (ZERO-LAG POSTMESSAGE)
; ------------------------------------------------------------

DispatchKeepAlive(isManual := false)
{
    global IsRunning, KeyVKMap, SelectedKeyIndex, TotalPulsesSent, ActiveSessionsMap

    if (!IsRunning && !isManual)
        return

    try hwnds := WinGetList("ahk_class SAP_FRONTEND_SESSION")
    catch
        return

    targetVK := KeyVKMap[SelectedKeyIndex]
    now := A_Now
    currentHwnds := Map()
    sentThisBatch := 0

    for hwnd in hwnds
    {
        currentHwnds[hwnd] := true

        if !ActiveSessionsMap.Has(hwnd)
        {
            title := ""
            try title := WinGetTitle("ahk_id " hwnd)
            ActiveSessionsMap[hwnd] := {
                System: ExtractSystem(title),
                Title: title,
                FirstSeen: now,
                LastPingTime: now,
                PingCount: 0
            }
        }

        try
        {
            ; Asynchronous postmessage - never waits or blocks
            PostMessage(0x0100, targetVK, 0, , "ahk_id " hwnd)           ; WM_KEYDOWN
            PostMessage(0x0101, targetVK, 0xC0000001, , "ahk_id " hwnd) ; WM_KEYUP

            info := ActiveSessionsMap[hwnd]
            info.LastPingTime := now
            info.PingCount++
            sentThisBatch++
            TotalPulsesSent++
        }
    }

    ; Prune closed windows
    for trackedHwnd, _ in ActiveSessionsMap.Clone()
    {
        if !currentHwnds.Has(trackedHwnd)
            ActiveSessionsMap.Delete(trackedHwnd)
    }

    UpdateTrayTipStats()

    if (isManual)
    {
        TrayTip("SAP Keep-Alive", "Sent " KeyNameMap[SelectedKeyIndex] " to " sentThisBatch " session(s)!", 1)
    }
}

ExtractSystem(title)
{
    if RegExMatch(title, "([A-Za-z0-9_-]+)\s*\(\d+\)", &match)
        return match[1]
    return "SAP"
}

ResetExpireTimer()
{
    global ExpireHours, ExpireEndTime
    if (ExpireHours > 0)
    {
        ExpireEndTime := DateAdd(A_Now, ExpireHours, "Hours")
    }
    else
    {
        ExpireEndTime := ""
    }
}

CheckExpiration()
{
    global ExpireEndTime, IsRunning
    if (ExpireEndTime != "" && A_Now >= ExpireEndTime)
    {
        IsRunning := false
        SetTimer(DispatchKeepAlive, 0)
        TrayTip("SAP Keep-Alive", "Auto-Expire duration reached. Keep-Alive is now PAUSED.", 3)
        UpdateTrayTipStats()
    }
}

FormatDuration(seconds)
{
    hrs := Floor(seconds / 3600)
    mins := Floor(Mod(seconds, 3600) / 60)
    secs := Mod(seconds, 60)
    if (hrs > 0)
        return hrs "h " mins "m " secs "s"
    if (mins > 0)
        return mins "m " secs "s"
    return secs "s"
}

UpdateTrayTipStats()
{
    global TotalPulsesSent, ActiveSessionsMap, SelectedKeyIndex, KeyNameMap, IsRunning, IntervalMinutes
    keyName := StrSplit(KeyNameMap[SelectedKeyIndex], " ")[1]
    statusStr := IsRunning ? ("Every " IntervalMinutes "m (" keyName ")") : "PAUSED"
    A_IconTip := "SAP Keep-Alive: " statusStr "`n"
               . "Active Sessions: " ActiveSessionsMap.Count "`n"
               . "Total Pulses: " TotalPulsesSent
}

; ------------------------------------------------------------
; SETTINGS & DASHBOARD GUI WINDOW
; ------------------------------------------------------------

global SettingsGui := ""

ShowSettingsWindow()
{
    global SettingsGui, IntervalMinutes, ExpireHours, SelectedKeyIndex, KeyNameMap, IsRunning
    global ScriptStartTime, TotalPulsesSent, ActiveSessionsMap, ExpireEndTime

    if (SettingsGui != "")
    {
        SettingsGui.Show()
        return
    }

    SettingsGui := Gui("+AlwaysOnTop", "SAP Keep-Alive Settings & Dashboard")
    SettingsGui.SetFont("s9", "Segoe UI")

    ; --- SECTION 1: SETTINGS ---
    SettingsGui.Add("GroupBox", "x15 y10 w450 h170", " Keep-Alive Settings ")

    SettingsGui.Add("Text", "x30 y35 w210", "Send Keystroke Every (Minutes):")
    editInterval := SettingsGui.Add("Edit", "x250 y32 w70 Number", IntervalMinutes)
    SettingsGui.Add("UpDown", "Range1-60", IntervalMinutes)

    SettingsGui.Add("Text", "x30 y70 w210", "Auto-Expire / Stop After (Hours):")
    editExpire := SettingsGui.Add("Edit", "x250 y67 w70 Number", ExpireHours)
    SettingsGui.Add("UpDown", "Range0-24", ExpireHours)
    SettingsGui.Add("Text", "x330 y70 w120 cGray", "(0 = Never Expire)")

    SettingsGui.Add("Text", "x30 y105 w210", "Keep-Alive Key:")
    choiceKey := SettingsGui.Add("DropDownList", "x250 y102 w180 Choose" SelectedKeyIndex, KeyNameMap)

    btnSave := SettingsGui.Add("Button", "x250 y138 w180 h28 Default", "Apply Settings")
    btnSave.OnEvent("Click", (*) => ApplySettings(editInterval.Value, editExpire.Value, choiceKey.Value))

    ; --- SECTION 2: LIVE MONITOR & DASHBOARD ---
    SettingsGui.Add("GroupBox", "x15 y190 w450 h190", " Live Status & Session Tracker ")

    txtStatus := SettingsGui.Add("Text", "x30 y215 w420", "Status: " (IsRunning ? "ACTIVE" : "PAUSED"))
    txtUptime := SettingsGui.Add("Text", "x30 y235 w420", "Tool Running: Calculating...")
    txtExpire := SettingsGui.Add("Text", "x30 y255 w420", "Auto-Expire In: Calculating...")
    txtPulses := SettingsGui.Add("Text", "x30 y275 w420", "Total Pulses Dispatched: " TotalPulsesSent)

    sessionList := SettingsGui.Add("ListView", "x30 y300 w420 h70", ["System", "HWND", "Alive Duration", "Pings Sent"])
    sessionList.ModifyCol(1, 70)
    sessionList.ModifyCol(2, 80)
    sessionList.ModifyCol(3, 140)
    sessionList.ModifyCol(4, 90)

    ; --- SECTION 3: QUICK ACTIONS ---
    btnPing := SettingsGui.Add("Button", "x15 y390 w140 h30", "Ping All Sessions Now")
    btnPing.OnEvent("Click", (*) => DispatchKeepAlive(true))

    btnToggle := SettingsGui.Add("Button", "x170 y390 w140 h30", IsRunning ? "Pause Keep-Alive" : "Resume Keep-Alive")
    btnToggle.OnEvent("Click", (*) => ToggleRunning(btnToggle, txtStatus))

    btnClose := SettingsGui.Add("Button", "x325 y390 w140 h30", "Close Window")
    btnClose.OnEvent("Click", (*) => SettingsGui.Hide())

    SettingsGui.OnEvent("Close", (*) => (SettingsGui := "", true))

    ; Refresher for GUI stats while open
    SetTimer(RefreshGuiStats, 1000)

    RefreshGuiStats()
    {
        if (SettingsGui = "" || !WinExist("ahk_id " SettingsGui.Hwnd))
        {
            SetTimer(RefreshGuiStats, 0)
            return
        }

        now := A_Now
        txtStatus.Text := "Status: " (IsRunning ? ("ACTIVE (Every " IntervalMinutes " min)") : "PAUSED")
        txtUptime.Text := "Tool Running: " FormatDuration(DateDiff(now, ScriptStartTime, "Seconds"))
        
        if (ExpireEndTime != "" && IsRunning)
        {
            remainSec := DateDiff(ExpireEndTime, now, "Seconds")
            txtExpire.Text := "Auto-Expire In: " (remainSec > 0 ? FormatDuration(remainSec) : "Expired")
        }
        else
        {
            txtExpire.Text := "Auto-Expire In: Disabled (Runs indefinitely)"
        }

        txtPulses.Text := "Total Pulses Dispatched: " TotalPulsesSent

        ; Refresh ListView
        sessionList.Delete()
        for hwnd, data in ActiveSessionsMap
        {
            aliveSec := DateDiff(now, data.FirstSeen, "Seconds")
            sessionList.Add(, data.System, hwnd, FormatDuration(aliveSec), data.PingCount)
        }
    }

    SettingsGui.Show("w480 h435")
}

ApplySettings(newInterval, newExpire, newKeyIndex)
{
    global IntervalMinutes, ExpireHours, SelectedKeyIndex, IsRunning

    IntervalMinutes := Integer(newInterval)
    ExpireHours     := Integer(newExpire)
    SelectedKeyIndex := Integer(newKeyIndex)

    ResetExpireTimer()

    if (IsRunning)
    {
        SetTimer(DispatchKeepAlive, IntervalMinutes * 60 * 1000)
    }

    UpdateTrayTipStats()
    TrayTip("SAP Keep-Alive", "Settings applied successfully!`nInterval: " IntervalMinutes "m | Expire: " ExpireHours "h", 1)
}

ToggleRunning(btnCtrl, txtStatusCtrl)
{
    global IsRunning, IntervalMinutes
    IsRunning := !IsRunning

    if (IsRunning)
    {
        SetTimer(DispatchKeepAlive, IntervalMinutes * 60 * 1000)
        btnCtrl.Text := "Pause Keep-Alive"
        TrayTip("SAP Keep-Alive", "Keep-Alive RESUMED", 1)
    }
    else
    {
        SetTimer(DispatchKeepAlive, 0)
        btnCtrl.Text := "Resume Keep-Alive"
        TrayTip("SAP Keep-Alive", "Keep-Alive PAUSED", 1)
    }

    UpdateTrayTipStats()
}

; ------------------------------------------------------------
; TRAY MENU SETUP (NO PHYSICAL HOTKEYS ALLOCATED)
; ------------------------------------------------------------

BuildTray()
{
    A_TrayMenu.Delete()
    A_TrayMenu.Add("Settings & Dashboard...", (*) => ShowSettingsWindow())
    A_TrayMenu.Add("Send Keep-Alive Now", (*) => DispatchKeepAlive(true))
    A_TrayMenu.Add()
    A_TrayMenu.Add("Exit", (*) => ExitApp())
    A_TrayMenu.Default := "Settings & Dashboard..."
}

BuildTray()
