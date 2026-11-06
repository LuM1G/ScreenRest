// ScreenRest - fake screen-off utility.
// Shows a borderless black window on every display and keeps the system
// awake (no automatic display-off, sleep or lock) while covered.
// Toggle with a global hotkey; exit from the tray menu.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

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
        public CoverForm(Screen screen)
        {
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
        private bool covering;
        private bool killLeds = true; // turn off RGB LEDs via OpenRGB on blackout

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

            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Items.Add(toggleItem);
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
            topMostTimer.Tick += delegate { AssertTopMost(); };
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

            if (killLeds) TryKillLeds();
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
                Native.SetWindowPos(cover.Handle, Native.HWND_TOPMOST, 0, 0, 0, 0,
                    Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
            }
        }

        private void ExitApp()
        {
            if (covering) Restore();
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
