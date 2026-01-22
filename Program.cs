using System;
using System.Diagnostics;
using System.Drawing;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

namespace IPWidget
{
    internal static class Program
    {
        [STAThread]
        static void Main()
        {
            ApplicationConfiguration.Initialize();
            Application.Run(new OverlayForm());
        }
    }

    public class OverlayForm : Form
    {
        // Settings
        private const int RefreshSeconds = 10;
        private const bool ShowPublicIP = true;   // public IP may be N/A in corporate networks
        private const bool ShowTime = true;
        private const bool ClickThrough = false; // set true if you want click-through widget

        private bool _alwaysOnTop = true;
        private static readonly Point StartPos = new Point(30, 30);

        private const int PadX = 14;
        private const int PadY = 12;
        private const int MinW = 220;
        private const int MinH = 70;

        private readonly Label _label;
        private readonly System.Windows.Forms.Timer _timer;

        private static readonly HttpClient http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(3)
        };

        // Startup (HKCU Run)
        private const string RunKeyPath = "Software\\Microsoft\\Windows\\CurrentVersion\\Run";
        private const string RunValueName = "IPWidget";

        public OverlayForm()
        {
            // Window
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = true;
            StartPosition = FormStartPosition.Manual;
            Location = StartPos;

            BackColor = Color.Magenta;
            TransparencyKey = Color.Magenta;
            DoubleBuffered = true;

            TopMost = _alwaysOnTop;

            _label = new Label
            {
                AutoSize = true,
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 11, FontStyle.Regular),
                BackColor = Color.Transparent
            };
            Controls.Add(_label);

            // Context menu
            var menu = new ContextMenuStrip();

            var itemTopMost = new ToolStripMenuItem("Always on top");
            itemTopMost.Checked = _alwaysOnTop;
            itemTopMost.Click += (s, e) =>
            {
                _alwaysOnTop = !_alwaysOnTop;
                TopMost = _alwaysOnTop;
                itemTopMost.Checked = _alwaysOnTop;
            };
            menu.Items.Add(itemTopMost);

            var itemStartup = new ToolStripMenuItem("Run at Windows startup");
            itemStartup.Checked = IsStartupEnabled();
            itemStartup.Click += (s, e) =>
            {
                bool newState = !IsStartupEnabled();
                SetStartupEnabled(newState);
                itemStartup.Checked = newState;
            };
            menu.Items.Add(itemStartup);

            menu.Items.Add(new ToolStripSeparator());

            menu.Items.Add("Copy IPs", null, async (s, e) =>
            {
                var ips = await GetIPsAsync();
                string local = ips.local;
                string pub = ips.pub;
                if (ShowPublicIP)
                    Clipboard.SetText("Local: " + local + "\r\nPublic: " + pub);
                else
                    Clipboard.SetText("Local: " + local);
            });

            menu.Items.Add("Refresh", null, async (s, e) => await RefreshAsync());
            menu.Items.Add("Exit", null, (s, e) => Close());

            ContextMenuStrip = menu;

            // Drag move (only if not click-through)
            MouseDown += (s, e) =>
            {
                if (e.Button == MouseButtons.Left && !ClickThrough)
                {
                    ReleaseCapture();
                    SendMessage(Handle, WM_NCLBUTTONDOWN, HTCAPTION, 0);
                }
            };

            // Timer
            _timer = new System.Windows.Forms.Timer();
            _timer.Interval = RefreshSeconds * 1000;
            _timer.Tick += async (s, e) => await RefreshAsync();
            _timer.Start();

            Shown += async (s, e) => await RefreshAsync();
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                if (ClickThrough)
                {
                    cp.ExStyle |= WS_EX_TRANSPARENT | WS_EX_LAYERED;
                }
                return cp;
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using (var bg = new SolidBrush(Color.FromArgb(150, 0, 0, 0)))
            {
                e.Graphics.FillRectangle(bg, new Rectangle(0, 0, Width, Height));
            }
        }

        private async Task RefreshAsync()
        {
            var ips = await GetIPsAsync();
            string local = ips.local;
            string pub = ips.pub;

            string text;
            if (ShowPublicIP)
                text = "Local  " + local + "\nPublic " + pub;
            else
                text = "Local  " + local;

            if (ShowTime)
                text += "\n" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

            _label.Text = text;
            _label.Location = new Point(PadX, PadY);

            var size = TextRenderer.MeasureText(
                _label.Text,
                _label.Font,
                new Size(2000, 2000),
                TextFormatFlags.TextBoxControl | TextFormatFlags.WordBreak
            );

            int w = Math.Max(size.Width + PadX * 2, MinW);
            int h = Math.Max(size.Height + PadY * 2, MinH);

            Size = new Size(w, h);
            Invalidate();
        }

        private static async Task<(string local, string pub)> GetIPsAsync()
        {
            string local = GetLocalIPv4FromIpconfig() ?? "N/A";
            string pub = ShowPublicIP ? await GetPublicIPAsync() : "-";
            return (local, pub);
        }

        private static string? GetLocalIPv4FromIpconfig()
        {
            string output = RunCmd("ipconfig");
            if (string.IsNullOrWhiteSpace(output)) return null;

            var lines = output.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var line in lines)
            {
                if (line.IndexOf("IPv4", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    var m = Regex.Match(line, @"(\d{1,3}\.){3}\d{1,3}");
                    if (m.Success) return m.Value;
                }
            }

            var m2 = Regex.Match(output, @"(\d{1,3}\.){3}\d{1,3}");
            return m2.Success ? m2.Value : null;
        }

        private static string RunCmd(string command)
        {
            try
            {
                var psi = new ProcessStartInfo("cmd.exe", "/c " + command);
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;

                using (var p = Process.Start(psi))
                {
                    if (p == null) return "";
                    string stdout = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(2000);
                    return stdout;
                }
            }
            catch
            {
                return "";
            }
        }

        private static async Task<string> GetPublicIPAsync()
        {
            try
            {
                var s = (await http.GetStringAsync("https://api.ipify.org")).Trim();
                if (string.IsNullOrWhiteSpace(s)) return "N/A";
                return s;
            }
            catch
            {
                return "N/A";
            }
        }

        // Startup toggle (HKCU Run)
        private static bool IsStartupEnabled()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false))
                {
                    var val = key?.GetValue(RunValueName) as string;
                    return !string.IsNullOrWhiteSpace(val);
                }
            }
            catch
            {
                return false;
            }
        }

        private static void SetStartupEnabled(bool enable)
        {
            try
            {
                RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, true);
                if (key == null)
                    key = Registry.CurrentUser.CreateSubKey(RunKeyPath, true);

                if (key == null) return;

                using (key)
                {
                    if (enable)
                    {
                        string exePath = Application.ExecutablePath;
                        string quoted = "\"" + exePath + "\"";
                        key.SetValue(RunValueName, quoted);
                    }
                    else
                    {
                        key.DeleteValue(RunValueName, false);
                    }
                }
            }
            catch
            {
                // Some corporate policies may block this. In that case, use shell:startup shortcut.
            }
        }

        // Win32 drag-move
        private const int WM_NCLBUTTONDOWN = 0x00A1;
        private const int HTCAPTION = 0x0002;

        [DllImport("user32.dll")]
        private static extern bool ReleaseCapture();

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int Msg, int wParam, int lParam);

        // Click-through styles
        private const int WS_EX_TRANSPARENT = 0x20;
        private const int WS_EX_LAYERED = 0x80000;
    }
}
