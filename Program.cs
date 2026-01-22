using System;
using System.Diagnostics;
using System.Drawing;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;

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
        private readonly Label _label;
        private readonly Timer _timer;

        // ===== 설정 =====
        private const int RefreshSeconds = 10;          // 갱신 주기
        private const bool ShowPublicIP = true;         // 공인 IP 표시
        private const bool ClickThrough = false;        // 클릭 통과(위젯처럼)
        private const bool AlwaysOnTop = true;          // 항상 위
        private static readonly Point StartPos = new Point(30, 30); // 위치
        private const int PadX = 16;
        private const int PadY = 14;

        private static readonly HttpClient http = new HttpClient()
        {
            Timeout = TimeSpan.FromSeconds(3)
        };

        public OverlayForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = true;
            TopMost = AlwaysOnTop;
            StartPosition = FormStartPosition.Manual;
            Location = StartPos;

            BackColor = Color.Magenta;
            TransparencyKey = Color.Magenta;
            DoubleBuffered = true;

            _label = new Label
            {
                AutoSize = true,
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 12, FontStyle.Regular),
                BackColor = Color.Transparent
            };
            Controls.Add(_label);

            var menu = new ContextMenuStrip();
            menu.Items.Add("복사(IP)", null, (_, __) => CopyIPsToClipboard());
            menu.Items.Add("새로고침", null, async (_, __) => await RefreshAsync());
            menu.Items.Add("닫기", null, (_, __) => Close());
            ContextMenuStrip = menu;

            MouseDown += (_, e) =>
            {
                if (e.Button == MouseButtons.Left && !ClickThrough)
                {
                    ReleaseCapture();
                    SendMessage(Handle, WM_NCLBUTTONDOWN, HTCAPTION, 0);
                }
            };

            _timer = new Timer { Interval = RefreshSeconds * 1000 };
            _timer.Tick += async (_, __) => await RefreshAsync();
            _timer.Start();

            Shown += async (_, __) => await RefreshAsync();
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                if (ClickThrough) cp.ExStyle |= WS_EX_TRANSPARENT | WS_EX_LAYERED;
                return cp;
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using var bg = new SolidBrush(Color.FromArgb(140, 0, 0, 0));
            e.Graphics.FillRectangle(bg, new Rectangle(0, 0, Width, Height));
        }

        private async Task RefreshAsync()
        {
            string local = GetLocalIPv4FromIpconfig() ?? "N/A";
            string pub = ShowPublicIP ? await GetPublicIPAsync() : "—";
            string time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

            string text = ShowPublicIP
                ? $"Local  : {local}\nPublic : {pub}\n{time}"
                : $"Local  : {local}\n{time}";

            _label.Text = text;
            _label.Location = new Point(PadX, PadY);

            var size = TextRenderer.MeasureText(_label.Text, _label.Font,
                new Size(1000, 1000), TextFormatFlags.TextBoxControl | TextFormatFlags.WordBreak);

            int w = Math.Max(size.Width + PadX * 2, 260);
            int h = Math.Max(size.Height + PadY * 2, 90);

            Size = new Size(w, h);
            Invalidate();
        }

        private void CopyIPsToClipboard()
        {
            string local = GetLocalIPv4FromIpconfig() ?? "N/A";
            string pub = "N/A";
            if (ShowPublicIP)
            {
                try { pub = GetPublicIPAsync().GetAwaiter().GetResult(); }
                catch { pub = "N/A"; }
            }

            Clipboard.SetText(ShowPublicIP
                ? $"Local: {local}\r\nPublic: {pub}"
                : $"Local: {local}");
        }

        private static string? GetLocalIPv4FromIpconfig()
        {
            string output = RunCmd("ipconfig");
            if (string.IsNullOrWhiteSpace(output)) return null;

            foreach (var line in output.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (line.Contains("IPv4", StringComparison.OrdinalIgnoreCase))
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
                var psi = new ProcessStartInfo("cmd.exe", "/c " + command)
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var p = Process.Start(psi);
                if (p == null) return "";
                string stdout = p.StandardOutput.ReadToEnd();
                p.WaitForExit(2000);
                return stdout;
            }
            catch { return ""; }
        }

        private static async Task<string> GetPublicIPAsync()
        {
            try
            {
                var s = (await http.GetStringAsync("https://api.ipify.org")).Trim();
                return string.IsNullOrWhiteSpace(s) ? "N/A" : s;
            }
            catch { return "N/A"; }
        }

        private const int WM_NCLBUTTONDOWN = 0x00A1;
        private const int HTCAPTION = 0x0002;

        [DllImport("user32.dll")] private static extern bool ReleaseCapture();
        [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hWnd, int Msg, int wParam, int lParam);

        private const int WS_EX_TRANSPARENT = 0x20;
        private const int WS_EX_LAYERED = 0x80000;
    }
}
