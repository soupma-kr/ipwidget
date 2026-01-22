using System;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
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
        // ===== Settings =====
        private const int NetRefreshSeconds = 5;        // 네트워크/인터넷 상태 갱신(요청대로 5초)
        private const bool ClickThrough = false;        // 배경처럼 클릭 통과 원하면 true
        private bool _alwaysOnTop = true;               // 항상 위 토글
        private static readonly Point StartPos = new Point(30, 30);

        private const int PadX = 14;
        private const int PadY = 12;
        private const int MinW = 240;
        private const int MinH = 72;

        // Startup (HKCU Run)
        private const string RunKeyPath = "Software\\Microsoft\\Windows\\CurrentVersion\\Run";
        private const string RunValueName = "IPWidget";

        private readonly Label _label;
        private readonly System.Windows.Forms.Timer _clockTimer;   // 1초마다 시간 갱신
        private readonly System.Windows.Forms.Timer _netTimer;     // 5초마다 네트워크 갱신

        // 캐시(매초 전체 검사 안 하려고)
        private string _localIp = "N/A";
        private string _netName = "N/A";
        private string _internet = "Unknown";

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

            menu.Items.Add("Copy", null, (s, e) =>
            {
                Clipboard.SetText("Local: " + _localIp + "\r\nNet: " + _netName + "\r\nInternet: " + _internet);
            });

            menu.Items.Add("Refresh now", null, async (s, e) =>
            {
                await RefreshNetworkAsync();
                RefreshTextAndSize();
            });

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

            // Timers
            _clockTimer = new System.Windows.Forms.Timer();
            _clockTimer.Interval = 1000;
            _clockTimer.Tick += (s, e) => RefreshTextAndSize();
            _clockTimer.Start();

            _netTimer = new System.Windows.Forms.Timer();
            _netTimer.Interval = NetRefreshSeconds * 1000;
            _netTimer.Tick += async (s, e) =>
            {
                await RefreshNetworkAsync();
                RefreshTextAndSize();
            };
            _netTimer.Start();

            Shown += async (s, e) =>
            {
                await RefreshNetworkAsync();
                RefreshTextAndSize();
            };
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

        private void RefreshTextAndSize()
        {
            string time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

            string text =
                "Local   " + _localIp + "\n" +
                "Net     " + _netName + "\n" +
                "Internet " + _internet + "\n" +
                time;

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

        private async Task RefreshNetworkAsync()
        {
            _localIp = GetLocalIPv4FromIpconfig() ?? "N/A";
            _netName = GetActiveNetworkName() ?? "N/A";
            _internet = await CheckInternetByDnsAsync();
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

        private static string? GetActiveNetworkName()
        {
            try
            {
                // Pick an "up" interface with a default gateway and not loopback/tunnel
                var nics = NetworkInterface.GetAllNetworkInterfaces()
                    .Where(n =>
                        n.OperationalStatus == OperationalStatus.Up &&
                        n.NetworkInterfaceType != NetworkInterfaceType.Loopback &&
                        n.NetworkInterfaceType != NetworkInterfaceType.Tunnel);

                foreach (var nic in nics)
                {
                    var ipProps = nic.GetIPProperties();
                    bool hasGateway = ipProps.GatewayAddresses != null && ipProps.GatewayAddresses.Count > 0 &&
                                      ipProps.GatewayAddresses.Any(g => g.Address != null && !IPAddress.IsLoopback(g.Address) && g.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);

                    if (hasGateway)
                        return nic.Name; // e.g., "Ethernet", "Wi-Fi", VPN adapter name
                }

                // Fallback: first up interface name
                var firstUp = nics.FirstOrDefault();
                return firstUp?.Name;
            }
            catch
            {
                return null;
            }
        }

        // A) DNS-only check: fast and usually allowed in corp networks
        private static async Task<string> CheckInternetByDnsAsync()
        {
            try
            {
                // Windows NCSI DNS test host (commonly used by Windows to detect connectivity)
                // If DNS resolves, we treat as "OK" per your A choice.
                var entry = await Dns.GetHostEntryAsync("dns.msftncsi.com");
                if (entry != null && entry.AddressList != null && entry.AddressList.Length > 0)
                    return "OK";
                return "Restricted";
            }
            catch
            {
                return "No Internet";
            }
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
                // If blocked by policy, use shell:startup shortcut instead.
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
