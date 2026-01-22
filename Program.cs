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
