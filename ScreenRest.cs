// ScreenRest - fake screen-off utility.
// Shows a borderless black window on every display and keeps the system
// awake (no automatic display-off, sleep or lock) while covered.
// Toggle with a global hotkey; exit from the tray menu.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace ScreenRest
{
    internal static class Native
    {
        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll")]
        public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern uint SetThreadExecutionState(uint esFlags);

        [DllImport("user32.dll")]
        public static extern bool SetProcessDPIAware();

        [DllImport("user32.dll")]
        public static extern bool SetProcessDpiAwarenessContext(IntPtr dpiContext);

        [DllImport("user32.dll")]
        public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
            int x, int y, int cx, int cy, uint uFlags);

        public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        public static readonly IntPtr DPI_PER_MONITOR_V2 = new IntPtr(-4);

        public const uint SWP_NOMOVE = 0x0002;
        public const uint SWP_NOSIZE = 0x0001;
        public const uint SWP_NOACTIVATE = 0x0010;

        public const uint ES_CONTINUOUS = 0x80000000;
        public const uint ES_SYSTEM_REQUIRED = 0x00000001;
        public const uint ES_DISPLAY_REQUIRED = 0x00000002;

        public const uint MOD_ALT = 0x0001;
        public const uint MOD_CONTROL = 0x0002;
        public const uint MOD_SHIFT = 0x0004;
        public const uint MOD_WIN = 0x0008;

        public const int WM_HOTKEY = 0x0312;
        public const int WM_SYSCOMMAND = 0x0112;
        public const int SC_CLOSE = 0xF060;
        [DllImport("user32.dll")]
        public static extern IntPtr SetCursor(IntPtr hCursor);

        public const int WM_SETCURSOR = 0x0020;
        public const int WS_EX_TOOLWINDOW = 0x00000080;

        // ---- physical monitor / DDC-CI (dxva2) ----
        [DllImport("user32.dll")]
        public static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

        [DllImport("dxva2.dll", SetLastError = true)]
        public static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, ref uint count);

        [DllImport("dxva2.dll", SetLastError = true)]
        public static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr hMonitor,
            uint arraySize, [Out] PHYSICAL_MONITOR[] monitors);

        [DllImport("dxva2.dll", SetLastError = true)]
        public static extern bool DestroyPhysicalMonitors(uint arraySize,
            [In] PHYSICAL_MONITOR[] monitors);

        [DllImport("dxva2.dll", SetLastError = true)]
        public static extern bool GetVCPFeatureAndVCPFeatureReply(IntPtr hMonitor, byte vcpCode,
            out uint type, out uint current, out uint maximum);

        [DllImport("dxva2.dll", SetLastError = true)]
        public static extern bool SetVCPFeature(IntPtr hMonitor, byte vcpCode, uint value);

        // ---- DWM window attributes (Windows 11 corners / border) ----
        [DllImport("dwmapi.dll")]
        public static extern int DwmSetWindowAttribute(IntPtr hWnd, int attribute,
            ref int value, int size);

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct PHYSICAL_MONITOR
        {
            public IntPtr hPhysicalMonitor;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string szPhysicalMonitorDescription;
        }

        public const uint MONITOR_DEFAULTTONEAREST = 0x00000002;
        public const byte VCP_BRIGHTNESS = 0x10;

        public const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        public const int DWMWA_BORDER_COLOR = 34;
        public const int DWMWCP_DONOTROUND = 1;
        public const int DWMWA_COLOR_NONE = unchecked((int)0xFFFFFFFE);
    }

    // Message-only window that receives the global hotkey.
    internal sealed class HotKeyWindow : NativeWindow
    {
        public event EventHandler HotKeyPressed;

        public HotKeyWindow()
        {
            CreateHandle(new CreateParams());
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Native.WM_HOTKEY)
            {
                EventHandler handler = HotKeyPressed;
                if (handler != null) handler(this, EventArgs.Empty);
                return;
            }
            base.WndProc(ref m);
        }
    }

    // Borderless black window covering one display.
    internal sealed class CoverForm : Form
    {
        private readonly Screen screen;

        public CoverForm(Screen screen)
        {
            this.screen = screen;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            Bounds = screen.Bounds;
            BackColor = Color.Black;
            ShowInTaskbar = false;
            TopMost = true;
            KeyPreview = true;
            DoubleBuffered = true;
            Text = "ScreenRestCover";
        }

        public Screen TargetScreen
        {
            get { return screen; }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);

            // Windows 11 rounds borderless windows and paints a thin system
            // border; those cut-outs let the desktop show through at the
            // corners. Ask DWM for square corners and no border. Older Windows
            // versions simply reject these attributes.
            try
            {
                int corners = Native.DWMWCP_DONOTROUND;
                Native.DwmSetWindowAttribute(Handle, Native.DWMWA_WINDOW_CORNER_PREFERENCE,
                    ref corners, sizeof(int));
                int border = Native.DWMWA_COLOR_NONE;
                Native.DwmSetWindowAttribute(Handle, Native.DWMWA_BORDER_COLOR,
                    ref border, sizeof(int));
            }
            catch (Exception) { }
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= Native.WS_EX_TOOLWINDOW; // keep out of Alt+Tab
                return cp;
            }
        }

        protected override void WndProc(ref Message m)
        {
            // Hide the mouse cursor while the pointer is over the cover.
            if (m.Msg == Native.WM_SETCURSOR)
            {
                Native.SetCursor(IntPtr.Zero);
                m.Result = (IntPtr)1;
                return;
            }
            // Block Alt+F4 / system close so the cover cannot be dismissed by accident.
            if (m.Msg == Native.WM_SYSCOMMAND && (m.WParam.ToInt64() & 0xFFF0) == Native.SC_CLOSE)
            {
                return;
            }
            base.WndProc(ref m);
        }
    }

    // Drops the physical backlight to zero while the cover is up.
    // A black window is still lit by the LCD backlight; only the monitor's own
    // brightness setting can remove that glow. External displays are driven
    // through DDC/CI, the internal laptop panel through WMI. Original values
    // are written to a small state file before anything is changed, so a
    // crash or a forced kill can be repaired on the next launch.
    internal static class BrightnessController
    {
        private static readonly object gate = new object();
        private static bool dimmed;
        private static bool wantDim;
        private static List<SavedValue> activeSaved;

        private sealed class SavedValue
        {
            public string DeviceName; // null for the WMI (laptop panel) entry
            public int Value;
        }

        // DDC/CI calls take tens of milliseconds per monitor, so run them in
        // the background; the cover must appear instantly.
        public static void DimAsync(Action<string> onProblem)
        {
            wantDim = true;
            ThreadPool.QueueUserWorkItem(delegate { Dim(onProblem); });
        }

        private static void Dim(Action<string> onProblem)
        {
            string problem = null;
            lock (gate)
            {
                if (!wantDim || dimmed) return;

                List<SavedValue> saved = new List<SavedValue>();
                List<Native.PHYSICAL_MONITOR[]> openHandles = new List<Native.PHYSICAL_MONITOR[]>();
                List<IntPtr> dimTargets = new List<IntPtr>();
                int screensHandled = 0;

                foreach (Screen screen in Screen.AllScreens)
                {
                    uint count;
                    Native.PHYSICAL_MONITOR[] monitors = OpenPhysicalMonitors(screen, out count);
                    if (monitors == null) continue;

                    bool savedThisScreen = false;
                    foreach (Native.PHYSICAL_MONITOR monitor in monitors)
                    {
                        uint type;
                        uint current;
                        uint maximum;
                        if (!Native.GetVCPFeatureAndVCPFeatureReply(monitor.hPhysicalMonitor,
                                Native.VCP_BRIGHTNESS, out type, out current, out maximum)
                            || maximum == 0)
                        {
                            continue;
                        }
                        saved.Add(new SavedValue
                        {
                            DeviceName = screen.DeviceName,
                            Value = (int)Math.Min(current, maximum)
                        });
                        dimTargets.Add(monitor.hPhysicalMonitor);
                        savedThisScreen = true;
                    }

                    if (savedThisScreen)
                    {
                        screensHandled++;
                        openHandles.Add(monitors);
                    }
                    else
                    {
                        Native.DestroyPhysicalMonitors(count, monitors);
                    }
                }

                // Some panel (usually the laptop's internal one) ignored DDC/CI;
                // WMI is the fallback for it. Read the old value first so it can
                // always be restored.
                bool savedWmi = false;
                if (screensHandled < Screen.AllScreens.Length)
                {
                    int wmiBrightness;
                    if (TryReadWmiBrightness(out wmiBrightness))
                    {
                        saved.Add(new SavedValue { DeviceName = null, Value = wmiBrightness });
                        savedWmi = true;
                    }
                }

                if (saved.Count > 0) SaveState(saved);
                activeSaved = saved;

                foreach (IntPtr handle in dimTargets)
                {
                    Native.SetVCPFeature(handle, Native.VCP_BRIGHTNESS, 0);
                }
                foreach (Native.PHYSICAL_MONITOR[] monitors in openHandles)
                {
                    Native.DestroyPhysicalMonitors((uint)monitors.Length, monitors);
                }

                if (savedWmi) TrySetWmiBrightness(0);

                if (saved.Count == 0)
                {
                    problem = "Backlight dimming is not available on this display " +
                              "(DDC/CI may be disabled in the monitor menu).";
                }
                else
                {
                    dimmed = true;
                }
            }
            if (onProblem != null) onProblem(problem);
        }

        public static void Restore()
        {
            wantDim = false;
            lock (gate)
            {
                if (!dimmed) return;
                List<SavedValue> remaining = RestoreValues(activeSaved);
                activeSaved = null;
                dimmed = false;
                if (remaining.Count == 0) DeleteState();
                else SaveState(remaining);
            }
        }

        // Repairs a brightness left at zero by an earlier crash or taskkill.
        public static void RestoreOnStartup()
        {
            lock (gate)
            {
                if (!File.Exists(StatePath())) return;
                List<SavedValue> remaining = RestoreValues(LoadState());
                if (remaining.Count == 0) DeleteState();
                else SaveState(remaining);
            }
        }

        private static List<SavedValue> RestoreValues(List<SavedValue> values)
        {
            List<SavedValue> remaining = new List<SavedValue>();
            if (values == null) return remaining;
            foreach (SavedValue value in values)
            {
                bool ok;
                if (value.DeviceName == null) ok = TrySetWmiBrightness(value.Value);
                else ok = TrySetDdcBrightness(value.DeviceName, value.Value);
                if (!ok) remaining.Add(value);
            }
            return remaining;
        }

        private static Native.PHYSICAL_MONITOR[] OpenPhysicalMonitors(Screen screen, out uint count)
        {
            count = 0;
            Native.POINT point = new Native.POINT();
            point.X = screen.Bounds.Left + screen.Bounds.Width / 2;
            point.Y = screen.Bounds.Top + screen.Bounds.Height / 2;
            IntPtr hMonitor = Native.MonitorFromPoint(point, Native.MONITOR_DEFAULTTONEAREST);
            if (hMonitor == IntPtr.Zero) return null;
            if (!Native.GetNumberOfPhysicalMonitorsFromHMONITOR(hMonitor, ref count) || count == 0)
            {
                return null;
            }

            Native.PHYSICAL_MONITOR[] monitors = new Native.PHYSICAL_MONITOR[count];
            if (!Native.GetPhysicalMonitorsFromHMONITOR(hMonitor, count, monitors)) return null;
            return monitors;
        }

        private static bool TrySetDdcBrightness(string deviceName, int value)
        {
            Screen target = null;
            foreach (Screen screen in Screen.AllScreens)
            {
                if (screen.DeviceName == deviceName)
                {
                    target = screen;
                    break;
                }
            }
            if (target == null) return false;

            uint count;
            Native.PHYSICAL_MONITOR[] monitors = OpenPhysicalMonitors(target, out count);
            if (monitors == null) return false;

            bool applied = false;
            try
            {
                foreach (Native.PHYSICAL_MONITOR monitor in monitors)
                {
                    if (Native.SetVCPFeature(monitor.hPhysicalMonitor,
                            Native.VCP_BRIGHTNESS, (uint)value))
                    {
                        applied = true;
                    }
                }
            }
            finally
            {
                Native.DestroyPhysicalMonitors(count, monitors);
            }
            return applied;
        }

        private static bool TryReadWmiBrightness(out int value)
        {
            value = -1;
            try
            {
                using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(
                    "root\\wmi", "SELECT CurrentBrightness FROM WmiMonitorBrightness"))
                using (ManagementObjectCollection results = searcher.Get())
                {
                    foreach (ManagementBaseObject item in results)
                    {
                        object current = item["CurrentBrightness"];
                        if (current != null)
                        {
                            value = Convert.ToInt32(current);
                            return true;
                        }
                    }
                }
            }
            catch (Exception) { }
            return false;
        }

        private static bool TrySetWmiBrightness(int value)
        {
            if (value < 0) value = 0;
            if (value > 100) value = 100;
            try
            {
                bool any = false;
                using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(
                    "root\\wmi", "SELECT * FROM WmiMonitorBrightnessMethods"))
                using (ManagementObjectCollection results = searcher.Get())
                {
                    foreach (ManagementObject item in results)
                    {
                        try
                        {
                            item.InvokeMethod("WmiSetBrightness",
                                new object[] { (uint)0, (byte)value });
                            any = true;
                        }
                        catch (Exception) { }
                    }
                }
                return any;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static string StatePath()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ScreenRest", "brightness.state");
        }

        private static void SaveState(List<SavedValue> values)
        {
            try
            {
                string path = StatePath();
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                List<string> lines = new List<string>();
                lines.Add("v1");
                foreach (SavedValue value in values)
                {
                    if (value.DeviceName == null) lines.Add("wmi|" + value.Value);
                    else lines.Add("ddc|" + value.DeviceName + "|" + value.Value);
                }
                File.WriteAllLines(path, lines.ToArray());
            }
            catch (Exception) { }
        }

        private static List<SavedValue> LoadState()
        {
            List<SavedValue> values = new List<SavedValue>();
            try
            {
                string path = StatePath();
                if (!File.Exists(path)) return values;
                foreach (string line in File.ReadAllLines(path))
                {
                    string[] parts = line.Split('|');
                    int value;
                    if (parts.Length == 3 && parts[0] == "ddc"
                        && int.TryParse(parts[2], out value))
                    {
                        values.Add(new SavedValue { DeviceName = parts[1], Value = value });
                    }
                    else if (parts.Length == 2 && parts[0] == "wmi"
                        && int.TryParse(parts[1], out value))
                    {
                        values.Add(new SavedValue { DeviceName = null, Value = value });
                    }
                }
            }
            catch (Exception) { }
            return values;
        }

        private static void DeleteState()
        {
            try { File.Delete(StatePath()); } catch (Exception) { }
        }
    }

    internal sealed class MainContext : ApplicationContext
    {
        // ---- hotkey configuration: edit these two lines and rebuild ----
        private const uint HotKeyModifiers = Native.MOD_CONTROL | Native.MOD_ALT;
        private const uint HotKeyVk = 0x4D; // virtual-key code, 0x4D = 'M'
        private const string HotKeyLabel = "Ctrl+Alt+M";
        // ----------------------------------------------------------------

        private const int HotKeyId = 0x5C7; // any id unique within this process

        private readonly NotifyIcon tray;
        private readonly ToolStripMenuItem toggleItem;
        private readonly HotKeyWindow hotKeyWindow;
        private readonly System.Windows.Forms.Timer topMostTimer;
        private readonly ToolStripMenuItem killLedsItem;
        private readonly ToolStripMenuItem dimItem;
        private bool covering;
        private bool killLeds = true; // turn off RGB LEDs via OpenRGB on blackout
        private bool dimBacklight = true; // drop monitor brightness while covered
        private volatile string pendingBrightnessProblem;
        private bool warnedBrightnessProblem;

        public MainContext()
        {
            hotKeyWindow = new HotKeyWindow();
            hotKeyWindow.HotKeyPressed += OnHotKey;

            toggleItem = new ToolStripMenuItem("Off (" + HotKeyLabel + ")");
            toggleItem.Click += delegate { Toggle(); };

            ToolStripMenuItem exitItem = new ToolStripMenuItem("Exit");
            exitItem.Click += delegate { ExitApp(); };

            killLedsItem = new ToolStripMenuItem("Kill RGB LEDs (OpenRGB)");
            killLedsItem.CheckOnClick = true;
            killLedsItem.Checked = true;
            killLedsItem.Click += delegate { killLeds = killLedsItem.Checked; };

            dimItem = new ToolStripMenuItem("Dim backlight while covered");
            dimItem.CheckOnClick = true;
            dimItem.Checked = true;
            dimItem.Click += delegate
            {
                dimBacklight = dimItem.Checked;
                if (!dimBacklight) BrightnessController.Restore();
                else if (covering) DimBacklightNow();
            };

            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Items.Add(toggleItem);
            menu.Items.Add(dimItem);
            menu.Items.Add(killLedsItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(exitItem);

            tray = new NotifyIcon();
            tray.Icon = MakeTrayIcon();
            tray.ContextMenuStrip = menu;
            tray.Text = ""; // no tooltip, keep a low profile
            tray.Visible = true;

            if (!Native.RegisterHotKey(hotKeyWindow.Handle, HotKeyId, HotKeyModifiers, HotKeyVk))
            {
                MessageBox.Show(
                    "Hotkey " + HotKeyLabel + " is already taken by another program.\n" +
                    "Edit HotKeyModifiers / HotKeyVk in ScreenRest.cs and rebuild.",
                    "ScreenRest", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                ExitApp();
                return;
            }

            topMostTimer = new System.Windows.Forms.Timer();
            topMostTimer.Interval = 1000;
            topMostTimer.Tick += delegate { AssertTopMost(); ShowBrightnessProblem(); };
            topMostTimer.Start();
        }

        private void OnHotKey(object sender, EventArgs e)
        {
            Toggle();
        }

        private void Toggle()
        {
            if (covering) Restore();
            else Cover();
        }

        private void Cover()
        {
            // Keep the system awake: no display-off, no sleep, no lock while covered.
            Native.SetThreadExecutionState(
                Native.ES_CONTINUOUS | Native.ES_SYSTEM_REQUIRED | Native.ES_DISPLAY_REQUIRED);

            foreach (Screen screen in Screen.AllScreens)
            {
                CoverForm cover = new CoverForm(screen);
                cover.Show();
                cover.Activate(); // take focus so keystrokes land on the cover
            }
            covering = true;
            toggleItem.Text = "Restore (" + HotKeyLabel + ")";

            // The covers are already black; dim the backlights afterwards so
            // the screen goes dark without delaying the hotkey.
            if (dimBacklight) DimBacklightNow();

            if (killLeds) TryKillLeds();
        }

        private void DimBacklightNow()
        {
            BrightnessController.DimAsync(delegate(string problem)
            {
                pendingBrightnessProblem = problem;
            });
        }

        // Best-effort RGB blackout via OpenRGB. Silent on any failure:
        // LED control is a bonus and must never break the main function.
        private void TryKillLeds()
        {
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    string exe = FindOpenRgb();
                    if (exe == null) return;
                    using (Process p = Process.Start(new ProcessStartInfo
                    {
                        FileName = exe,
                        Arguments = "--mode static --color 000000",
                        UseShellExecute = false,
                        CreateNoWindow = true
                    }))
                    {
                        if (p != null && !p.WaitForExit(5000))
                        {
                            try { p.Kill(); } catch { }
                        }
                    }
                }
                catch { }
            });
        }

        private static string FindOpenRgb()
        {
            string[] candidates = new[]
            {
                "openrgb.exe", // resolved via PATH
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "OpenRGB", "openrgb.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "OpenRGB", "openrgb.exe")
            };
            foreach (string path in candidates)
            {
                if (File.Exists(path)) return path;
            }
            return null;
        }

        private void Restore()
        {
            // Bring the backlights back while the covers are still black, then
            // remove the covers; otherwise the desktop would flash up at
            // brightness zero.
            BrightnessController.Restore();

            List<Form> toClose = new List<Form>();
            foreach (Form form in Application.OpenForms)
            {
                if (form is CoverForm) toClose.Add(form);
            }
            foreach (Form form in toClose)
            {
                form.Close();
                form.Dispose();
            }

            // Hand the idle timers back to Windows.
            Native.SetThreadExecutionState(Native.ES_CONTINUOUS);

            covering = false;
            toggleItem.Text = "Off (" + HotKeyLabel + ")";
        }

        private void AssertTopMost()
        {
            if (!covering) return;
            foreach (Form form in Application.OpenForms)
            {
                CoverForm cover = form as CoverForm;
                if (cover == null) continue;
                Rectangle bounds = cover.TargetScreen.Bounds;
                if (cover.Bounds != bounds) cover.Bounds = bounds;
                Native.SetWindowPos(cover.Handle, Native.HWND_TOPMOST, 0, 0, 0, 0,
                    Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
            }
        }

        private void ShowBrightnessProblem()
        {
            string problem = pendingBrightnessProblem;
            if (problem == null) return;
            pendingBrightnessProblem = null;
            if (warnedBrightnessProblem) return;
            warnedBrightnessProblem = true;
            tray.BalloonTipTitle = "ScreenRest";
            tray.BalloonTipText = problem;
            tray.ShowBalloonTip(5000);
        }

        private void ExitApp()
        {
            if (covering) Restore();
            else BrightnessController.Restore();
            Native.UnregisterHotKey(hotKeyWindow.Handle, HotKeyId);
            if (tray != null)
            {
                tray.Visible = false;
                tray.Dispose();
            }
            Application.Exit();
        }

        private static Icon MakeTrayIcon()
        {
            using (Bitmap bmp = new Bitmap(16, 16))
            {
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.Clear(Color.FromArgb(64, 64, 64));
                }
                return Icon.FromHandle(bmp.GetHicon());
            }
        }
    }

    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            EnableDpiAwareness();

            bool createdNew;
            using (Mutex mutex = new Mutex(true, "Local\\ScreenRest.SingleInstance", out createdNew))
            {
                if (!createdNew) return;
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

                // A previous run may have been killed while the backlights were
                // at zero; put them back before doing anything else.
                BrightnessController.RestoreOnStartup();
                Application.ThreadException += delegate { BrightnessController.Restore(); };
                AppDomain.CurrentDomain.UnhandledException += delegate { BrightnessController.Restore(); };
                AppDomain.CurrentDomain.ProcessExit += delegate { BrightnessController.Restore(); };
                SystemEvents.SessionEnding += delegate { BrightnessController.Restore(); };

                Application.Run(new MainContext());
            }
        }

        private static void EnableDpiAwareness()
        {
            // Per-monitor v2 keeps Screen.Bounds in physical pixels on mixed-DPI setups.
            // The API only exists on Windows 10 1703+; fall back for older systems.
            try
            {
                if (!Native.SetProcessDpiAwarenessContext(Native.DPI_PER_MONITOR_V2))
                {
                    Native.SetProcessDPIAware();
                }
            }
            catch (EntryPointNotFoundException)
            {
                Native.SetProcessDPIAware();
            }
        }
    }
}
