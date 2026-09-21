using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace SapKeepAlive
{
    static class Program
    {
        public const string CurrentVersion = "v2.1.1";
        public const string GitHubRepo = "Hackers-lab/SapKeepAlive";
        public const string GitHubReleasesUrl = "https://github.com/Hackers-lab/SapKeepAlive/releases";
        public const string GitHubReleasesLatestUrl = "https://github.com/Hackers-lab/SapKeepAlive/releases/latest";
        public const string RawVersionUrl = "https://raw.githubusercontent.com/Hackers-lab/SapKeepAlive/main/version.txt";

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        private const uint WM_KEYDOWN = 0x0100;
        private const uint WM_KEYUP = 0x0101;
        private const uint WM_CLOSE = 0x0010;

        private const int VK_SHIFT = 0x10;
        private const int VK_F8 = 0x77;
        private const int VK_F13 = 0x7C;

        // Settings
        public static int IntervalMinutes = 5;
        public static int ExpireHours = 8;
        public static int TargetVK = VK_SHIFT;
        public static string TargetKeyName = "Shift (Silent Safe)";
        public static bool IsRunning = true;
        public static bool DailyResetEnabled = true;

        // Auto Close SAP sessions time (e.g., 19:00 for 7:00 PM)
        public static bool EnableAutoClose = false;
        public static DateTime AutoCloseTime = DateTime.Today.AddHours(19); // 7:00 PM default
        private static bool HasClosedToday = false;
        private static DateTime LastActiveDay = DateTime.Today;

        public static DateTime StartTime = DateTime.Now;
        public static DateTime? ExpireTime = DateTime.Now.AddHours(8);
        public static int TotalPulses = 0;

        public class SessionInfo
        {
            public IntPtr Hwnd;
            public string SystemId;
            public string Title;
            public DateTime FirstSeen;
            public DateTime LastPing;
            public int PingCount;
        }

        public static Dictionary<IntPtr, SessionInfo> Sessions = new Dictionary<IntPtr, SessionInfo>();

        public static NotifyIcon Tray;
        public static System.Windows.Forms.Timer KeepAliveTimer;
        public static System.Windows.Forms.Timer BackgroundPoller;
        public static SettingsForm Dashboard;

        private const string RunRegistryKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string AppSettingsRegistryKey = @"Software\SapKeepAlive";
        private const string AppName = "SapKeepAlive";

        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // Load saved settings if any
            LoadSettings();

            // Set TLS 1.2 for modern GitHub API HTTPS communication
            try
            {
                ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072 | SecurityProtocolType.Tls11 | SecurityProtocolType.Tls;
            }
            catch {}

            // Create Tray Icon
            Tray = new NotifyIcon();
            Tray.Icon = CreateAppIcon();
            Tray.Text = "SAP Multi-Session Keep-Alive (" + CurrentVersion + ")";
            Tray.Visible = true;

            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Items.Add("Settings & Dashboard...", null, (s, e) => ShowDashboard());
            menu.Items.Add("Ping All Sessions Now", null, (s, e) => DispatchKeepAlive(true));
            menu.Items.Add("Check for Updates...", null, (s, e) => CheckForUpdates(true));
            menu.Items.Add("Close / Logout All SAP Sessions Now", null, (s, e) => {
                if (MessageBox.Show("Are you sure you want to close and log out all active SAP sessions now?", 
                    "Confirm Close All", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
                {
                    int closed = CloseAllSAPSessions();
                    MessageBox.Show("Closed " + closed + " SAP session(s).", "Sessions Closed", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            });
            menu.Items.Add("-");
            menu.Items.Add("Exit", null, (s, e) => {
                Tray.Visible = false;
                Application.Exit();
            });

            Tray.ContextMenuStrip = menu;
            Tray.DoubleClick += (s, e) => ShowDashboard();

            // Keep Alive Timer
            KeepAliveTimer = new System.Windows.Forms.Timer();
            KeepAliveTimer.Interval = Math.Max(1000, IntervalMinutes * 60 * 1000);
            KeepAliveTimer.Tick += (s, e) => DispatchKeepAlive(false);
            KeepAliveTimer.Start();

            // Fast Background Poller (Runs every 3 seconds)
            // Automatically discovers SAP sessions as soon as they open,
            // checks daily expiration, and handles auto-close schedule
            BackgroundPoller = new System.Windows.Forms.Timer();
            BackgroundPoller.Interval = 3000;
            BackgroundPoller.Tick += (s, e) => BackgroundTick();
            BackgroundPoller.Start();

            // Run initial discovery
            RefreshActiveSessions();

            // Startup notification
            Tray.ShowBalloonTip(4000, "SAP Keep-Alive " + CurrentVersion + " is Running",
                "Running silently in your taskbar hidden icons.\nClick here or right-click the icon for Settings & Dashboard.",
                ToolTipIcon.Info);

            Tray.BalloonTipClicked += (s, e) => ShowDashboard();

            // Check for updates asynchronously on startup
            ThreadPool.QueueUserWorkItem(state => {
                Thread.Sleep(5000);
                CheckForUpdates(false);
            });

            Application.Run();
        }

        public static void ResetTimer()
        {
            KeepAliveTimer.Stop();
            if (IsRunning && IntervalMinutes > 0)
            {
                KeepAliveTimer.Interval = Math.Max(1000, IntervalMinutes * 60 * 1000);
                KeepAliveTimer.Start();
            }
        }

        public static void ResetExpireTime()
        {
            if (ExpireHours > 0)
            {
                ExpireTime = DateTime.Now.AddHours(ExpireHours);
                IsRunning = true;
                ResetTimer();
            }
            else
            {
                ExpireTime = null;
                IsRunning = true;
                ResetTimer();
            }
        }

        private static void BackgroundTick()
        {
            DateTime now = DateTime.Now;

            // 1. Daily Reset Check (if a new day starts, reset expire timer and re-enable keep-alive)
            if (DailyResetEnabled && now.Date > LastActiveDay.Date)
            {
                LastActiveDay = now.Date;
                HasClosedToday = false;
                ResetExpireTime();
                Tray.ShowBalloonTip(2500, "SAP Keep-Alive", "New day detected. Keep-Alive timer has been automatically reset.", ToolTipIcon.Info);
            }

            // 2. Continuous Session Auto-Discovery
            RefreshActiveSessions();

            // 3. Auto Close Scheduler Check
            if (EnableAutoClose)
            {
                if (now.Hour == AutoCloseTime.Hour && now.Minute == AutoCloseTime.Minute)
                {
                    if (!HasClosedToday)
                    {
                        HasClosedToday = true;
                        int closed = CloseAllSAPSessions();
                        Tray.ShowBalloonTip(3500, "SAP Auto-Close Triggered", 
                            "Scheduled auto-close time reached (" + AutoCloseTime.ToString("hh:mm tt") + "). Closed " + closed + " active SAP session(s).", 
                            ToolTipIcon.Warning);
                    }
                }
                else
                {
                    if (now.Hour != AutoCloseTime.Hour)
                    {
                        HasClosedToday = false;
                    }
                }
            }

            // 4. Check Inactivity Expiration
            if (ExpireTime.HasValue && now >= ExpireTime.Value && IsRunning)
            {
                IsRunning = false;
                KeepAliveTimer.Stop();
                Tray.ShowBalloonTip(3000, "SAP Keep-Alive Paused", 
                    "Auto-Expire duration reached (" + ExpireHours + " hours). Keep-Alive is paused for today.", ToolTipIcon.Info);
            }
        }

        public static void RefreshActiveSessions()
        {
            List<IntPtr> hwnds = GetSAPWindows();
            DateTime now = DateTime.Now;
            HashSet<IntPtr> currentSet = new HashSet<IntPtr>(hwnds);

            foreach (IntPtr hwnd in hwnds)
            {
                StringBuilder sbTitle = new StringBuilder(512);
                GetWindowText(hwnd, sbTitle, sbTitle.Capacity);
                string title = sbTitle.ToString();

                if (!Sessions.ContainsKey(hwnd))
                {
                    string sys = "SAP";
                    Match m = Regex.Match(title, @"([A-Za-z0-9_-]+)\s*\(\d+\)");
                    if (m.Success) sys = m.Groups[1].Value;

                    Sessions[hwnd] = new SessionInfo
                    {
                        Hwnd = hwnd,
                        SystemId = sys,
                        Title = title,
                        FirstSeen = now,
                        LastPing = now,
                        PingCount = 0
                    };
                }
                else
                {
                    if (!string.IsNullOrEmpty(title))
                    {
                        Sessions[hwnd].Title = title;
                    }
                }
            }

            // Clean up closed windows
            List<IntPtr> toRemove = new List<IntPtr>();
            foreach (var kvp in Sessions)
            {
                if (!currentSet.Contains(kvp.Key))
                    toRemove.Add(kvp.Key);
            }
            foreach (var h in toRemove) Sessions.Remove(h);
        }

        public static int DispatchKeepAlive(bool manual)
        {
            if (!IsRunning && !manual) return 0;

            RefreshActiveSessions();
            DateTime now = DateTime.Now;
            int count = 0;

            foreach (var kvp in Sessions)
            {
                IntPtr hwnd = kvp.Key;
                SessionInfo s = kvp.Value;

                // Asynchronous zero-lag PostMessage
                PostMessage(hwnd, WM_KEYDOWN, (IntPtr)TargetVK, IntPtr.Zero);
                PostMessage(hwnd, WM_KEYUP, (IntPtr)TargetVK, (IntPtr)0xC0000001);

                s.LastPing = now;
                s.PingCount++;
                TotalPulses++;
                count++;
            }

            if (manual)
            {
                Tray.ShowBalloonTip(1500, "SAP Keep-Alive", "Sent " + TargetKeyName + " to " + count + " active session(s)!", ToolTipIcon.Info);
            }

            return count;
        }

        public static int CloseAllSAPSessions()
        {
            List<IntPtr> hwnds = GetSAPWindows();
            int closedCount = 0;
            foreach (IntPtr hwnd in hwnds)
            {
                try
                {
                    PostMessage(hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                    closedCount++;
                }
                catch {}
            }
            Sessions.Clear();
            return closedCount;
        }

        public static List<IntPtr> GetSAPWindows()
        {
            List<IntPtr> hwnds = new List<IntPtr>();
            EnumWindows((hWnd, lParam) => {
                StringBuilder sbClass = new StringBuilder(256);
                GetClassName(hWnd, sbClass, sbClass.Capacity);
                if (sbClass.ToString() == "SAP_FRONTEND_SESSION")
                {
                    hwnds.Add(hWnd);
                }
                return true;
            }, IntPtr.Zero);
            return hwnds;
        }

        public static void ShowDashboard()
        {
            if (Dashboard == null || Dashboard.IsDisposed)
            {
                Dashboard = new SettingsForm();
            }
            Dashboard.Show();
            Dashboard.BringToFront();
        }

        public static bool IsNewerVersion(string remoteVer, string currentVer)
        {
            try
            {
                Version rv = ParseVersion(remoteVer);
                Version cv = ParseVersion(currentVer);
                return rv > cv;
            }
            catch
            {
                return false;
            }
        }

        private static Version ParseVersion(string ver)
        {
            if (string.IsNullOrEmpty(ver)) return new Version(0, 0, 0);
            ver = ver.Trim().TrimStart('v', 'V');
            string[] parts = ver.Split('.');
            int major = parts.Length > 0 ? int.Parse(parts[0]) : 0;
            int minor = parts.Length > 1 ? int.Parse(parts[1]) : 0;
            int build = parts.Length > 2 ? int.Parse(parts[2]) : 0;
            return new Version(major, minor, build);
        }

        public static void CheckForUpdates(bool showPromptIfLatest)
        {
            try
            {
                string latestVersion = "";

                // Method 1: Check GitHub releases/latest redirect URL (instant, no CDN lag)
                try
                {
                    HttpWebRequest req = (HttpWebRequest)WebRequest.Create(GitHubReleasesLatestUrl);
                    req.UserAgent = "SapKeepAlive-Updater";
                    req.AllowAutoRedirect = false;
                    using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                    {
                        string loc = resp.GetResponseHeader("Location");
                        if (!string.IsNullOrEmpty(loc))
                        {
                            Match rm = Regex.Match(loc, @"/tag/([^/?#]+)");
                            if (rm.Success) latestVersion = rm.Groups[1].Value.Trim();
                        }
                    }
                }
                catch {}

                // Method 2: Check raw version.txt with cache-busting timestamp
                if (string.IsNullOrEmpty(latestVersion))
                {
                    try
                    {
                        using (WebClient client = new WebClient())
                        {
                            client.Headers.Add("User-Agent", "SapKeepAlive-Updater");
                            string remoteText = client.DownloadString(RawVersionUrl + "?t=" + DateTime.UtcNow.Ticks);
                            if (!string.IsNullOrEmpty(remoteText))
                            {
                                Match vm = Regex.Match(remoteText.Trim(), @"v?\d+\.\d+(\.\d+)?");
                                if (vm.Success)
                                {
                                    latestVersion = vm.Value.Trim();
                                    if (!latestVersion.StartsWith("v", StringComparison.OrdinalIgnoreCase))
                                        latestVersion = "v" + latestVersion;
                                }
                            }
                        }
                    }
                    catch {}
                }

                if (!string.IsNullOrEmpty(latestVersion))
                {
                    // Strict semantic version comparison: only alert if remote is actually GREATER than current
                    if (IsNewerVersion(latestVersion, CurrentVersion))
                    {
                        if (MessageBox.Show("A newer version of SAP Keep-Alive (" + latestVersion + ") is available!\n\nYour Version: " + CurrentVersion + "\n\nWould you like to open GitHub releases to download the update?", 
                            "Update Available", MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
                        {
                            Process.Start(GitHubReleasesUrl);
                        }
                        return;
                    }
                    else
                    {
                        if (showPromptIfLatest)
                        {
                            MessageBox.Show("You are running the latest version of SAP Keep-Alive (" + CurrentVersion + ").", 
                                "Up to Date", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        }
                        return;
                    }
                }

                if (showPromptIfLatest)
                {
                    MessageBox.Show("You are running the latest version of SAP Keep-Alive (" + CurrentVersion + ").", 
                        "Up to Date", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            }
            catch (Exception ex)
            {
                if (showPromptIfLatest)
                {
                    if (MessageBox.Show("Unable to reach update server (" + ex.Message + ").\n\nWould you like to open GitHub Releases in your browser?", 
                        "Update Check", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                    {
                        Process.Start(GitHubReleasesUrl);
                    }
                }
            }
        }

        public static bool IsAutoStartEnabled()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunRegistryKey, false))
                {
                    if (key != null)
                    {
                        object val = key.GetValue(AppName);
                        return val != null;
                    }
                }
            }
            catch {}
            return false;
        }

        public static void SetAutoStart(bool enable)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunRegistryKey, true))
                {
                    if (key != null)
                    {
                        if (enable)
                        {
                            string exePath = Application.ExecutablePath;
                            key.SetValue(AppName, "\"" + exePath + "\"");
                        }
                        else
                        {
                            key.DeleteValue(AppName, false);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("Could not update Windows Startup: " + ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        public static void SaveSettings()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(AppSettingsRegistryKey))
                {
                    if (key != null)
                    {
                        key.SetValue("IntervalMinutes", IntervalMinutes);
                        key.SetValue("ExpireHours", ExpireHours);
                        key.SetValue("TargetKeyName", TargetKeyName);
                        key.SetValue("DailyResetEnabled", DailyResetEnabled ? 1 : 0);
                        key.SetValue("EnableAutoClose", EnableAutoClose ? 1 : 0);
                        key.SetValue("AutoCloseHour", AutoCloseTime.Hour);
                        key.SetValue("AutoCloseMinute", AutoCloseTime.Minute);
                    }
                }
            }
            catch {}
        }

        public static void LoadSettings()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(AppSettingsRegistryKey, false))
                {
                    if (key != null)
                    {
                        IntervalMinutes = Convert.ToInt32(key.GetValue("IntervalMinutes", 5));
                        ExpireHours = Convert.ToInt32(key.GetValue("ExpireHours", 8));
                        TargetKeyName = Convert.ToString(key.GetValue("TargetKeyName", "Shift (Silent Safe)"));
                        if (TargetKeyName.StartsWith("Shift")) TargetVK = 0x10;
                        else if (TargetKeyName.StartsWith("F8")) TargetVK = 0x77;
                        else TargetVK = 0x7C;

                        DailyResetEnabled = Convert.ToInt32(key.GetValue("DailyResetEnabled", 1)) == 1;
                        EnableAutoClose = Convert.ToInt32(key.GetValue("EnableAutoClose", 0)) == 1;
                        int hr = Convert.ToInt32(key.GetValue("AutoCloseHour", 19));
                        int min = Convert.ToInt32(key.GetValue("AutoCloseMinute", 0));
                        AutoCloseTime = DateTime.Today.AddHours(hr).AddMinutes(min);

                        ResetExpireTime();
                    }
                }
            }
            catch {}
        }

        public static Icon CreateAppIcon()
        {
            try
            {
                string iconPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "app.ico");
                if (File.Exists(iconPath))
                {
                    return new Icon(iconPath);
                }
            }
            catch {}

            using (Bitmap b = new Bitmap(32, 32))
            using (Graphics g = Graphics.FromImage(b))
            {
                g.Clear(Color.FromArgb(0, 100, 200));
                using (Font f = new Font("Arial", 8, FontStyle.Bold))
                using (Brush br = new SolidBrush(Color.White))
                {
                    g.DrawString("SAP", f, br, new PointF(3, 9));
                }
                return Icon.FromHandle(b.GetHicon());
            }
        }

        public static string FormatDuration(TimeSpan ts)
        {
            if (ts.TotalHours >= 1)
                return string.Format("{0:0}h {1:0}m {2:0}s", ts.TotalHours, ts.Minutes, ts.Seconds);
            if (ts.TotalMinutes >= 1)
                return string.Format("{0:0}m {1:0}s", ts.TotalMinutes, ts.Seconds);
            return string.Format("{0:0}s", ts.Seconds);
        }
    }

    public class SettingsForm : Form
    {
        private NumericUpDown numInterval;
        private NumericUpDown numExpire;
        private ComboBox cbKey;
        private CheckBox chkDailyReset;
        private CheckBox chkAutoStart;
        private CheckBox chkAutoClose;
        private DateTimePicker dtpAutoClose;

        private Label lblStatus;
        private Label lblUptime;
        private Label lblExpire;
        private Label lblPulses;
        private ListView lvSessions;
        private Button btnToggle;
        private System.Windows.Forms.Timer refreshTimer;

        public SettingsForm()
        {
            this.Text = "SAP Keep-Alive Pro " + Program.CurrentVersion + " - Settings & Dashboard";
            this.Size = new Size(580, 715);
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.StartPosition = FormStartPosition.CenterScreen;
            this.TopMost = false;
            try { this.Icon = Program.CreateAppIcon(); } catch {}

            InitControls();
        }

        private void InitControls()
        {
            // Settings Group
            GroupBox gbSettings = new GroupBox();
            gbSettings.Text = " Keep-Alive & Automation Configuration ";
            gbSettings.Location = new Point(15, 10);
            gbSettings.Size = new Size(535, 255);

            // Row 1: Interval
            Label l1 = new Label { Text = "Send Keystroke Every (Minutes):", Location = new Point(20, 28), AutoSize = true };
            numInterval = new NumericUpDown { Location = new Point(255, 26), Width = 70, Minimum = 1, Maximum = 60, Value = Program.IntervalMinutes };

            // Row 2: Expire
            Label l2 = new Label { Text = "Auto-Expire Keep-Alive After (Hours):", Location = new Point(20, 58), AutoSize = true };
            numExpire = new NumericUpDown { Location = new Point(255, 56), Width = 70, Minimum = 0, Maximum = 24, Value = Program.ExpireHours };
            Label l2Note = new Label { Text = "(0 = Never Expire)", Location = new Point(335, 58), ForeColor = Color.Gray, AutoSize = true };

            // Row 3: Daily Reset
            chkDailyReset = new CheckBox { 
                Text = "Auto-Reset expire timer every morning / day (resumes keep-alive automatically)", 
                Location = new Point(20, 88), 
                AutoSize = true, 
                Checked = Program.DailyResetEnabled,
                ForeColor = Color.DarkBlue 
            };

            // Row 4: Key
            Label l3 = new Label { Text = "Keep-Alive Key:", Location = new Point(20, 118), AutoSize = true };
            cbKey = new ComboBox { Location = new Point(255, 115), Width = 180, DropDownStyle = ComboBoxStyle.DropDownList };
            cbKey.Items.AddRange(new object[] { "Shift (Silent Safe)", "F8 (Test Action)", "F13 (Hidden Ghost)" });
            cbKey.SelectedItem = Program.TargetKeyName;

            // Row 5: Auto Close Sessions Time
            chkAutoClose = new CheckBox { Text = "Auto-Close / Log out all SAP sessions at:", Location = new Point(20, 150), AutoSize = true, Checked = Program.EnableAutoClose };
            dtpAutoClose = new DateTimePicker { Location = new Point(285, 147), Width = 100, Format = DateTimePickerFormat.Time, ShowUpDown = true, Value = Program.AutoCloseTime };
            chkAutoClose.CheckedChanged += (s, e) => dtpAutoClose.Enabled = chkAutoClose.Checked;
            dtpAutoClose.Enabled = chkAutoClose.Checked;

            // Row 6: Auto-Start with Windows
            chkAutoStart = new CheckBox { Text = "Start automatically with Windows startup", Location = new Point(20, 180), AutoSize = true, Checked = Program.IsAutoStartEnabled() };

            // Buttons inside Settings
            Button btnResetTimerNow = new Button { Text = "Reset Timer Now", Location = new Point(20, 210), Size = new Size(130, 32) };
            btnResetTimerNow.Click += (s, e) => {
                Program.ResetExpireTime();
                MessageBox.Show("Keep-Alive duration reset and resumed!", "Timer Reset", MessageBoxButtons.OK, MessageBoxIcon.Information);
            };

            Button btnCheckUpdates = new Button { Text = "Check Updates", Location = new Point(160, 210), Size = new Size(120, 32) };
            btnCheckUpdates.Click += (s, e) => Program.CheckForUpdates(true);

            Button btnApply = new Button { Text = "Save Settings", Location = new Point(390, 210), Size = new Size(130, 32), Font = new Font(this.Font, FontStyle.Bold) };
            btnApply.Click += (s, e) => {
                Program.IntervalMinutes = (int)numInterval.Value;
                Program.ExpireHours = (int)numExpire.Value;
                Program.TargetKeyName = cbKey.SelectedItem.ToString();
                if (Program.TargetKeyName.StartsWith("Shift")) Program.TargetVK = 0x10;
                else if (Program.TargetKeyName.StartsWith("F8")) Program.TargetVK = 0x77;
                else Program.TargetVK = 0x7C;

                Program.DailyResetEnabled = chkDailyReset.Checked;
                Program.EnableAutoClose = chkAutoClose.Checked;
                Program.AutoCloseTime = dtpAutoClose.Value;

                Program.SetAutoStart(chkAutoStart.Checked);
                Program.SaveSettings();
                Program.ResetExpireTime();
                Program.ResetTimer();

                MessageBox.Show("Settings saved and applied successfully!", "SAP Keep-Alive", MessageBoxButtons.OK, MessageBoxIcon.Information);
            };

            gbSettings.Controls.AddRange(new Control[] { l1, numInterval, l2, numExpire, l2Note, chkDailyReset, l3, cbKey, chkAutoClose, dtpAutoClose, chkAutoStart, btnResetTimerNow, btnCheckUpdates, btnApply });

            // Dashboard Group
            GroupBox gbDash = new GroupBox();
            gbDash.Text = " Live Status & Auto-Discovered Sessions ";
            gbDash.Location = new Point(15, 275);
            gbDash.Size = new Size(535, 215);

            lblStatus = new Label { Location = new Point(20, 22), AutoSize = true, Font = new Font(this.Font, FontStyle.Bold) };
            lblUptime = new Label { Location = new Point(20, 43), AutoSize = true };
            lblExpire = new Label { Location = new Point(20, 64), AutoSize = true };
            lblPulses = new Label { Location = new Point(20, 85), AutoSize = true };

            lvSessions = new ListView { Location = new Point(20, 110), Size = new Size(495, 90), View = View.Details, FullRowSelect = true, GridLines = true };
            lvSessions.Columns.Add("System", 75);
            lvSessions.Columns.Add("HWND", 90);
            lvSessions.Columns.Add("Alive Duration", 140);
            lvSessions.Columns.Add("Pings Sent", 100);

            gbDash.Controls.AddRange(new Control[] { lblStatus, lblUptime, lblExpire, lblPulses, lvSessions });

            // Disclaimer Box
            GroupBox gbDisclaimer = new GroupBox();
            gbDisclaimer.Text = " Disclaimer & Notice ";
            gbDisclaimer.Location = new Point(15, 500);
            gbDisclaimer.Size = new Size(535, 80);

            Label lblDisclaimer = new Label {
                Text = "DISCLAIMER: This tool is provided for developer convenience to prevent inactivity logouts. Use at your own risk. Always ensure open transactions are saved before leaving your workstation or before scheduled auto-close times. The authors accept no liability for unsaved work or security compliance.",
                Location = new Point(15, 18),
                Size = new Size(505, 55),
                ForeColor = Color.FromArgb(120, 50, 0),
                Font = new Font(this.Font.FontFamily, 7.8f, FontStyle.Regular)
            };
            gbDisclaimer.Controls.Add(lblDisclaimer);

            // Bottom Buttons
            Button btnPing = new Button { Text = "Ping Sessions Now", Location = new Point(15, 595), Size = new Size(130, 35) };
            btnPing.Click += (s, e) => Program.DispatchKeepAlive(true);

            btnToggle = new Button { Text = "Pause Keep-Alive", Location = new Point(155, 595), Size = new Size(130, 35) };
            btnToggle.Click += (s, e) => {
                Program.IsRunning = !Program.IsRunning;
                Program.ResetTimer();
                btnToggle.Text = Program.IsRunning ? "Pause Keep-Alive" : "Resume Keep-Alive";
            };

            Button btnCloseSessions = new Button { Text = "Close SAP Sessions", Location = new Point(295, 595), Size = new Size(140, 35), ForeColor = Color.DarkRed };
            btnCloseSessions.Click += (s, e) => {
                if (MessageBox.Show("Are you sure you want to close and log out all active SAP sessions now?", 
                    "Confirm Close", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
                {
                    int closed = Program.CloseAllSAPSessions();
                    MessageBox.Show("Closed " + closed + " SAP session(s).", "Sessions Closed", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            };

            Button btnHide = new Button { Text = "Hide to Tray", Location = new Point(445, 595), Size = new Size(105, 35) };
            btnHide.Click += (s, e) => this.Hide();

            this.Controls.AddRange(new Control[] { gbSettings, gbDash, gbDisclaimer, btnPing, btnToggle, btnCloseSessions, btnHide });

            refreshTimer = new System.Windows.Forms.Timer { Interval = 1000 };
            refreshTimer.Tick += (s, e) => UpdateDashboardData();
            refreshTimer.Start();

            UpdateDashboardData();
        }

        private void UpdateDashboardData()
        {
            DateTime now = DateTime.Now;
            lblStatus.Text = "Status: " + (Program.IsRunning ? ("ACTIVE (Every " + Program.IntervalMinutes + " mins)") : "PAUSED");
            lblStatus.ForeColor = Program.IsRunning ? Color.DarkGreen : Color.Red;

            lblUptime.Text = "Tool Uptime: " + Program.FormatDuration(now - Program.StartTime);

            if (Program.ExpireTime.HasValue && Program.IsRunning)
            {
                TimeSpan rem = Program.ExpireTime.Value - now;
                lblExpire.Text = "Auto-Expire In: " + (rem.TotalSeconds > 0 ? Program.FormatDuration(rem) : "EXPIRED (Will reset tomorrow morning)");
            }
            else
            {
                lblExpire.Text = "Auto-Expire In: Disabled (Runs indefinitely)";
            }

            if (Program.EnableAutoClose)
            {
                lblExpire.Text += " | Auto-Close: " + Program.AutoCloseTime.ToString("hh:mm tt");
            }

            lblPulses.Text = "Total Pulses Dispatched: " + Program.TotalPulses;

            // Sessions
            lvSessions.Items.Clear();
            foreach (var kvp in Program.Sessions)
            {
                var s = kvp.Value;
                ListViewItem lvi = new ListViewItem(s.SystemId);
                lvi.SubItems.Add(s.Hwnd.ToString());
                lvi.SubItems.Add(Program.FormatDuration(now - s.FirstSeen));
                lvi.SubItems.Add(s.PingCount.ToString());
                lvSessions.Items.Add(lvi);
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                this.Hide();
            }
            base.OnFormClosing(e);
        }
    }
}
