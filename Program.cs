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
        // ===== 설정 =====
        private const int RefreshSeconds = 10;
        private const bool ShowPublicIP = true;      // 공인 IP 표시(회사망에서 막히면 N/A 가능)
        private const bool ShowTime = true;          // 시간 표시
        private const bool ClickThrough = false;     // 배경처럼 클릭 통과 (원하면 true)
        private bool _alwaysOnTop = true;            // 3번: 항상 위 토글
        private static readonly Point StartPos = new Point(30, 30);

        private const int PadX = 14;
        private const int PadY = 12;
        private const int MinW = 220;
        private const int MinH = 70;

        private readonly Label _label;
        private readonly System.Windows.Forms.Timer _timer;

        private static readonly HttpClient http = new HttpClient()
        {
            Timeout = TimeSpan.FromSeconds(3)
        };

        // 시작프로그램(레지스트리 Run) 키
        private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string RunValueName = "IPWidget";

        public OverlayForm()
        {
            // 창 기본
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = true;
            StartPosition = FormStartPosition.Manual;
            Location = StartPos;

            // 심플한 반투명 패널 느낌 (색상키 투명 + OnPaint에서 배경 그림)
            BackColor = Color.Magenta;
            TransparencyKey = Color.Magenta;
            DoubleBuffered = true;

            _label = new Label
            {
                AutoSize = true,
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 11, FontStyle.Regular),
                BackColor = Color.Transparent
            };
            Controls.Add(_label);

            // 우클릭 메뉴
            var menu = new ContextMenuStrip();

            var itemTopMost = new ToolStripMenuItem("항상 위") { Checked = _alwaysOnTop };
            itemTopMost.Click += (_, __) =>
            {
                _alwaysOnTop = !_alwaysOnTop;
                TopMost = _alwaysOnTop;
                itemTopMost.Checked = _alwaysOnTop;
            };
            menu.Items.Add(itemTopMost);

            var itemStartup = new ToolStripMenuItem("Windows 시작 시 실행") { Checked = IsStartupEnabled() };
            itemStartup.Click += (_, __) =>
            {
                bool newState = !IsStartupEnabled();
                SetStartupEnabled(newState);
                itemStartup.Checked = newState;
            };
            menu.Items.Add(itemStartup);

            menu.Items.Add(new ToolStripSeparator());

            menu.Items.Add("복사(IP)", null, async (_, __) =>
            {
                var (local, pub) = await GetIPsAsync();
                Clipboard.SetText(ShowPublicIP
                    ? $"Local: {local}\r\nPublic: {pub}"
                    : $"Local: {local}");
            });

            menu.Items.Add("새로고침", null, async (_, __) => await RefreshAsync());
            menu.Items.Add("종료", null, (_, __) => Close());

            ContextMenuStrip = menu;

            // 드래그로 이동 (ClickThrough=false일 때)
            MouseDown += (_, e) =>
            {
                if (e.Button == MouseButtons.Left && !ClickThrough)
                {
                    ReleaseCapture();
                    SendMessage(Handle, WM_NCLBUTTONDOWN, HTCAPTION, 0);
                }
            };

            // 클릭 통과 옵션
            TopMost = _alwaysOnTop;

            // 타이머
            _timer = new System.Windows.Forms.Timer { Interval = RefreshSeconds * 1000 };
            _timer.Tick += async (_, __) => await RefreshAsync();
            _timer.Start();

            Shown += async (_, __) => await RefreshAsync();
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                if (ClickThrough)
                    cp.ExStyle |= WS_EX_TRANSPARENT | WS_EX_LAYERED;
                return cp;
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);

            // 심플한 반투명 검정 배경
            using var bg = new SolidBrush(Color.FromArgb(150, 0, 0, 0));
            e.Graphics.FillRectangle(bg, new Rectangle(0, 0, Width, Height));
        }

        private async Task RefreshAsync()
        {
            var (local, pub) = await GetIPsAsync();
            string time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

            // 심플 2~3줄
            string text = ShowPublicIP
                ? $"Local  {local}\nPublic {pub}"
                : $"Local  {local}";

            if (ShowTime)
                text += $"\n{time}";

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
            string pub = ShowPublicIP ? await GetPublicIPAsync() : "—";
            return (local, pub);
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

        // ===== 부팅 자동 실행: HKCU Run 등록/해제 =====
        private static bool IsStartupEnabled()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
                var val = key?.GetValue(RunValueName) as string;
                return !string.IsNullOrWhiteSpace(val);
            }
            catch { return false; }
        }

        private void SetStartupEnabled(bool enable)
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
                              ?? Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);

                if (enable)
                {
                    // 현재 실행중인 exe 경로
                    string exePath = Application.ExecutablePath;

                    // 경로에 공백이 있을 수 있으므로 따옴표 포함
                    key.SetValue(RunValueName, $"\"{exePath}\"");
                }
                else
                {
                    key.DeleteValue(RunValueName, throwOnMissingValue: false);
                }
            }
            catch
            {
                // 회사 정책/권한으로 막히면 여기서 조용히 실패할 수 있음
                // 그 경우: 시작프로그램 폴더 방식(바로가기)로 해야 함
            }
        }

        // ===== Win32 (드래그 이동) =====
        private const int WM_NCLBUTTONDOWN = 0x00A1;
        private const int HTCAPTION = 0x0002;

        [DllImport("user32.dll")] private static extern bool ReleaseCapture();
        [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hWnd, int Msg, int wParam, int lParam);

        // Click-through
        private const int WS_EX_TRANSPARENT = 0x20;
        private const int WS_EX_LAYERED = 0x80000;
    }
}
2) IPWidget.csproj는 이 버전 권장 (이전 답변 그대로)
(이미 적용했으면 스킵)

xml
코드 복사
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>WinExe</OutputType>
    <TargetFramework>net8.0-windows</TargetFramework>
    <UseWindowsForms>true</UseWindowsForms>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>

    <PublishSingleFile>true</PublishSingleFile>
    <SelfContained>true</SelfContained>
    <RuntimeIdentifier>win-x64</RuntimeIdentifier>
    <EnableCompressionInSingleFile>true</EnableCompressionInSingleFile>
    <InvariantGlobalization>true</InvariantGlobalization>
  </PropertyGroup>
</Project>
