using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

namespace CodexBar
{
    internal sealed class CodexCard : Control
    {
        public CodexSnapshot Snapshot;
        public string Error;
        public bool Busy;
        public CodexCard()
        {
            Height = 206; AccessibleName = "Codex 剩余额度";
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
            using (GraphicsPath path = Palette.Round(new Rectangle(0, 0, Width - 1, Height - 1), 12))
            using (Brush fill = new SolidBrush(Palette.Surface)) g.FillPath(fill, path);
            Palette.TextAt(g, "Codex", Palette.Title, Palette.Text, new Rectangle(22, 16, 150, 28));
            string source = Snapshot != null && Snapshot.Source == "app-server" ? "官方在线额度" : "本机额度记录 · 备用";
            Palette.TextAt(g, source, Palette.Small, Palette.Muted, new Rectangle(180, 18, Width - 202, 24), TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
            BallRenderer.Draw(g, new RectangleF(22, 60, 84, 84), Snapshot != null && Snapshot.Weekly != null ? (double?)Snapshot.Weekly.Remaining : null,
                Snapshot != null && Snapshot.FiveHour != null ? (double?)Snapshot.FiveHour.Remaining : null,
                Error != null || Snapshot == null || Snapshot.Stale(DateTimeOffset.Now), false);
            DrawWindow(g, "周额度 · 内球", Snapshot == null ? null : Snapshot.Weekly, 61);
            DrawWindow(g, "5 小时 · 外圈", Snapshot == null ? null : Snapshot.FiveHour, 113);
            string status = Busy ? "正在读取…" : Error;
            Color color = status == null || Busy ? Palette.Muted : Palette.Warning;
            if (string.IsNullOrEmpty(status))
            {
                if (Snapshot == null) status = "等待同步 Codex 额度";
                else
                {
                    status = (Snapshot.Stale(DateTimeOffset.Now) ? "旧数据 · " : Snapshot.Source == "app-server" ? "同步于 " : "记录于 ") + Snapshot.RecordedAt.ToLocalTime().ToString("MM-dd HH:mm:ss") +
                        (string.IsNullOrEmpty(Snapshot.Notice) ? " · 每分钟刷新" : " · " + Snapshot.Notice);
                    if (Snapshot.Stale(DateTimeOffset.Now) || !string.IsNullOrEmpty(Snapshot.Notice)) color = Palette.Warning;
                }
            }
            Palette.TextAt(g, status, Palette.Small, color, new Rectangle(22, Height - 33, Width - 44, 22));
            AccessibleDescription = status;
        }
        private void DrawWindow(Graphics g, string label, QuotaWindow window, int y)
        {
            Palette.TextAt(g, label, Palette.Body, Palette.Muted, new Rectangle(128, y, 155, 22));
            bool expired = window != null && window.Expired(DateTimeOffset.Now);
            string value = window == null ? "未提供" : expired ? "待更新" : window.Remaining.ToString("0.#") + "% 剩余";
            Palette.TextAt(g, value, Palette.Title, expired ? Palette.Warning : Palette.Text, new Rectangle(288, y, Width - 310, 24), TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
            string reset = window != null && window.ResetsAt.HasValue ? "重置于 " + window.ResetsAt.Value.ToLocalTime().ToString("MM-dd HH:mm") : "重置时间未知";
            Palette.TextAt(g, reset, Palette.Small, Palette.Muted, new Rectangle(128, y + 25, Width - 150, 20));
        }
    }

    internal static class TrayMenus
    {
        public static ContextMenuStrip Create(Action openPanel, Action refresh, Action toggleBall, Action quit, bool useBall)
        {
            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Items.Add(new ToolStripMenuItem("Codex 额度与设置", null, delegate { openPanel(); }) { Name = "codexPanel" });
            menu.Items.Add(new ToolStripMenuItem("立即刷新", null, delegate { refresh(); }) { Name = "refresh" });
            menu.Items.Add(new ToolStripMenuItem("使用 Codex 状态球", null, delegate { toggleBall(); }) { Name = "codexBall", Checked = useBall });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem("退出", null, delegate { quit(); }) { Name = "quit" });
            return menu;
        }
    }

    internal sealed class CodexPanel : Form
    {
        private readonly bool preview;
        private readonly SettingsStore store;
        private Settings settings;
        private readonly CodexQuotaClient client = new CodexQuotaClient();
        private readonly CodexCard card = new CodexCard();
        private readonly Panel content = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        private readonly Button refresh = Palette.Button("立即刷新"), save = Palette.Button("保存设置"), close = Palette.Button("关闭");
        private readonly TextBox home = new TextBox { Name = "codexHome", Font = Palette.Body, BackColor = Palette.Surface, ForeColor = Palette.Text, BorderStyle = BorderStyle.FixedSingle };
        private readonly CheckBox showBall = Check("在托盘使用 Codex 状态球", "showCodexBall"), startup = Check("登录 Windows 后自动启动", "startWithWindows");
        private readonly NumericUpDown quota = new NumericUpDown { Name = "lowQuotaPercent", Minimum = 0, Maximum = 100, Value = 10, Font = Palette.Body };
        private readonly Label footer = new Label { ForeColor = Palette.Muted, Font = Palette.Small, Text = "关闭面板后继续在托盘监测" };
        private readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer { Interval = 60000 };
        private readonly HashSet<string> notified = new HashSet<string>();
        private NotifyIcon tray;
        private ContextMenuStrip menu;
        private Icon currentIcon;
        private bool busy, monitoringStarted, resourcesDisposed;
        private string failure;
        private int attempts;
        private long refreshElapsedMs;
        internal ContextMenuStrip TrayMenu { get { return menu; } }
        protected override bool ShowWithoutActivation { get { return preview; } }
        public event EventHandler ExitRequested;

        public CodexPanel(bool previewMode)
        {
            preview = previewMode;
            store = new SettingsStore(AppPaths.DataDirectory);
            try { settings = preview ? new Settings() : store.Load(); }
            catch { settings = new Settings(); footer.Text = "设置读取失败，请检查配置文件。"; }
            Text = preview ? "CodexBar preview" : "CodexBar"; Font = Palette.Body; ForeColor = Palette.Text; BackColor = Palette.Background;
            ClientSize = new Size(650, 640); MinimumSize = new Size(610, 440); FormBorderStyle = FormBorderStyle.FixedDialog;
            ShowInTaskbar = false; MaximizeBox = false; MinimizeBox = false; StartPosition = FormStartPosition.Manual; AutoScaleMode = AutoScaleMode.Dpi;
            Controls.Add(content); content.AutoScrollMinSize = new Size(0, 640);
            AddLabel("CodexBar", 24, 17, 560, 46, new Font("Segoe UI", 24F, FontStyle.Bold), Palette.Text);
            AddLabel("Codex 额度与设置 · v0.6", 26, 67, 560, 22, Palette.Body, Palette.Muted);
            card.SetBounds(24, 103, 602, 206); card.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top; content.Controls.Add(card);
            AddLabel("显示与提醒", 24, 328, 560, 26, Palette.Title, Palette.Text);
            showBall.SetBounds(24, 364, 570, 25); startup.SetBounds(24, 398, 570, 25);
            content.Controls.Add(showBall); content.Controls.Add(startup);
            AddLabel("剩余额度低于或等于", 24, 435, 150, 26, Palette.Body, Palette.Text);
            quota.SetBounds(178, 435, 74, 28); content.Controls.Add(quota);
            AddLabel("% 时提醒（周额度与 5 小时额度）", 262, 435, 360, 26, Palette.Body, Palette.Muted);
            AddLabel("Codex 数据目录", 24, 476, 560, 24, Palette.Body, Palette.Text);
            home.SetBounds(24, 505, 602, 28); home.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top; content.Controls.Add(home);
            AddLabel("优先在线查询，失败时使用此目录中的本机额度记录。", 24, 540, 602, 22, Palette.Small, Palette.Muted);
            refresh.SetBounds(24, 576, 100, 34); save.SetBounds(134, 576, 110, 34); close.SetBounds(548, 576, 78, 34);
            close.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            close.Name = "closePanel"; save.Name = "saveSettings";
            content.Controls.Add(refresh); content.Controls.Add(save); content.Controls.Add(close);
            footer.SetBounds(24, 616, 602, 22); footer.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right; content.Controls.Add(footer);
            refresh.Click += async delegate { await RefreshAll(); };
            save.Click += async delegate { await SaveSettings(); };
            close.Click += delegate { HidePanel(); };
            LoadSettingsControls();
            if (preview) home.Text = "%USERPROFILE%\\.codex";
            if (!preview)
            {
                try { MigrateStartup(); }
                catch { footer.Text = "开机启动项迁移失败，请在设置中重新保存。"; }
            }
            menu = TrayMenus.Create(OpenPanel, async delegate { await RefreshAll(); }, ToggleBall, Quit, settings.ShowCodexBall);
            if (!preview)
            {
                tray = new NotifyIcon { ContextMenuStrip = menu };
                // ContextMenuStrip handles right-click. There is deliberately no left-click or double-click action.
                UpdateTray(); tray.Visible = true;
                timer.Tick += async delegate { await RefreshAll(); };
            }
        }
        public void StartMonitoring()
        {
            if (preview || monitoringStarted) return;
            monitoringStarted = true;
            timer.Start();
            BeginInvoke(new Action(async delegate { await RefreshAll(); }));
        }
        private static CheckBox Check(string text, string name) { return new CheckBox { Text = text, Name = name, Font = Palette.Body, ForeColor = Palette.Text }; }
        private void AddLabel(string text, int x, int y, int w, int h, Font font, Color color)
        { content.Controls.Add(new Label { Text = text, Bounds = new Rectangle(x, y, w, h), Font = font, ForeColor = color, TextAlign = ContentAlignment.MiddleLeft }); }
        private void LoadSettingsControls()
        {
            home.Text = settings.CodexHome; showBall.Checked = settings.ShowCodexBall; startup.Checked = settings.StartWithWindows;
            quota.Value = Math.Max(0, Math.Min(100, settings.LowQuotaPercent));
        }
        private void OpenPanel()
        {
            LoadSettingsControls();
            Rectangle area = Screen.FromPoint(Cursor.Position).WorkingArea;
            Height = Math.Min(Height, Math.Max(420, area.Height - 24));
            Location = new Point(Math.Max(area.Left, area.Right - Width - 12), Math.Max(area.Top, area.Bottom - Height - 12));
            Show(); WindowState = FormWindowState.Normal; if (!preview) Activate();
            WriteRuntimeStatus();
        }
        private void HidePanel() { Hide(); LoadSettingsControls(); WriteRuntimeStatus(); }
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!preview && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; HidePanel(); }
            base.OnFormClosing(e);
        }
        protected override void WndProc(ref Message message)
        {
            // Re-launching the EXE must not open the panel; only the right-click menu does that.
            if (message.Msg == 0x8001) { WriteRuntimeStatus(); return; }
            if (message.Msg == 0x8002) { Quit(); return; }
            if (message.Msg == 0x8003) { BeginInvoke(new Action(async delegate { await RefreshAll(); })); return; }
            base.WndProc(ref message);
        }
        private void Quit() { if (ExitRequested != null) ExitRequested(this, EventArgs.Empty); else Dispose(); }
        private void ToggleBall()
        {
            Settings changed = settings.Copy(); changed.ShowCodexBall = !changed.ShowCodexBall;
            try { store.Save(changed); settings = changed; showBall.Checked = changed.ShowCodexBall; SyncBallMenu(); UpdateTray(); }
            catch { MessageBox.Show(this, "无法保存设置，请检查本地目录权限。", "CodexBar"); }
        }
        private void SyncBallMenu() { ((ToolStripMenuItem)menu.Items["codexBall"]).Checked = settings.ShowCodexBall; }
        private async Task SaveSettings()
        {
            if (preview || busy) return;
            if (!Path.IsPathRooted(home.Text.Trim())) { MessageBox.Show(this, "Codex 数据目录需要填写绝对路径。", "CodexBar"); return; }
            Settings changed = settings.Copy(); changed.CodexHome = home.Text.Trim(); changed.ShowCodexBall = showBall.Checked;
            changed.StartWithWindows = startup.Checked; changed.LowQuotaPercent = (int)quota.Value;
            try
            {
                store.Save(changed);
                if (changed.StartWithWindows != settings.StartWithWindows)
                {
                    try { SetStartup(changed.StartWithWindows); } catch { store.Save(settings); throw; }
                }
                bool newHome = changed.CodexHome != settings.CodexHome;
                settings = changed; if (newHome) { card.Snapshot = null; card.Error = null; }
                notified.Clear(); SyncBallMenu(); UpdateTray(); footer.Text = "设置已保存 · 关闭面板后继续监测";
            }
            catch { MessageBox.Show(this, "设置未能完整应用，请检查目录和开机启动权限。", "CodexBar"); return; }
            await RefreshAll();
        }
        private static void SetStartup(bool enabled)
        {
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"))
            {
                if (enabled) key.SetValue("CodexBar", "\"" + Application.ExecutablePath + "\" --background");
                else key.DeleteValue("CodexBar", false);
                string legacy = key.GetValue("AIAPIBar") as string;
                if (legacy != null && legacy.IndexOf("AIAPIBar.exe", StringComparison.OrdinalIgnoreCase) >= 0) key.DeleteValue("AIAPIBar", false);
            }
        }
        private void MigrateStartup()
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"))
            {
                string legacy = key == null ? null : key.GetValue("AIAPIBar") as string;
                if (settings.StartWithWindows || (legacy != null && legacy.IndexOf("AIAPIBar.exe", StringComparison.OrdinalIgnoreCase) >= 0)) SetStartup(settings.StartWithWindows);
            }
        }
        private static string FailureLabel(string stage, Exception error)
        {
            if (error is TimeoutException) return "查询超时";
            if (stage == "locate") return "无法定位 Codex CLI";
            if (stage == "auth-refresh") return "Codex 登录状态刷新失败";
            if (stage == "start") return "额度服务启动失败";
            if (stage == "parse") return "额度响应格式变化";
            if (error is IOException) return "额度服务连接断开";
            return "额度服务拒绝查询";
        }
        private async Task RefreshAll()
        {
            if (preview || busy || IsDisposed) return;
            Stopwatch clock = Stopwatch.StartNew();
            busy = true; card.Busy = true; refresh.Enabled = false; save.Enabled = false; card.Invalidate();
            attempts++; failure = null; WriteRuntimeStatus(); string codexHome = settings.CodexHome;
            try
            {
                CodexSnapshot next = null;
                for (int attempt = 0; attempt < 2 && next == null; attempt++)
                {
                    bool transient = false;
                    try { next = await client.Read(codexHome); failure = null; }
                    catch (Exception error)
                    {
                        failure = FailureLabel(client.Stage, error) + " (" + client.Stage + ":" + error.GetType().Name + ")";
                        transient = error is TimeoutException || error is IOException;
                    }
                    if (next != null || !transient || attempt == 1) break;
                    WriteRuntimeStatus(); await Task.Delay(2000); if (IsDisposed) return;
                }
                if (next == null)
                {
                    next = await Task.Run(() => CodexReader.Read(codexHome)); next.Notice = failure + " · 暂用本机记录";
                    if (card.Snapshot != null && card.Snapshot.Source == "app-server" && card.Snapshot.RecordedAt > next.RecordedAt)
                    { next = card.Snapshot; next.Notice = failure + " · 上次结果"; }
                }
                card.Snapshot = next; card.Error = null;
            }
            catch { card.Error = (failure ?? "读取额度失败") + " · 无有效本机记录"; }
            finally
            {
                busy = false;
                refreshElapsedMs = clock.ElapsedMilliseconds;
                if (!IsDisposed)
                {
                    card.Busy = false; refresh.Enabled = true; save.Enabled = true; card.Invalidate();
                    UpdateTray(); CheckNotifications(); WriteRuntimeStatus();
                }
            }
        }
        private void UpdateTray()
        {
            if (tray == null) return;
            CodexSnapshot snapshot = card.Error == null ? card.Snapshot : null;
            Icon next = BallRenderer.IconFor(snapshot, settings.ShowCodexBall); tray.Icon = next; Icon = next;
            Icon old = currentIcon; currentIcon = next; if (old != null) old.Dispose();
            string text = "CodexBar · Codex ";
            if (snapshot == null) text += "暂无有效记录";
            else if (snapshot.Stale(DateTimeOffset.Now)) text += "旧数据 · 右键查看";
            else text += "周 " + (snapshot.Weekly == null ? "?" : snapshot.Weekly.Remaining.ToString("0") + "%") + " | 5h " + (snapshot.FiveHour == null ? "?" : snapshot.FiveHour.Remaining.ToString("0") + "%");
            tray.Text = text.Length > 63 ? text.Substring(0, 63) : text;
        }
        private void CheckNotifications()
        {
            if (tray == null || card.Error != null || card.Snapshot == null || card.Snapshot.Stale(DateTimeOffset.Now)) return;
            NotifyLow("week", "Codex 周额度不足", card.Snapshot.Weekly != null && card.Snapshot.Weekly.Remaining <= settings.LowQuotaPercent);
            NotifyLow("five", "Codex 5 小时额度不足", card.Snapshot.FiveHour != null && card.Snapshot.FiveHour.Remaining <= settings.LowQuotaPercent);
        }
        private void NotifyLow(string id, string message, bool low)
        {
            if (!low) { notified.Remove(id); return; }
            if (notified.Add(id)) tray.ShowBalloonTip(5000, "CodexBar", message + "，右键状态球查看详情。", ToolTipIcon.Warning);
        }
        private void WriteRuntimeStatus()
        {
            if (preview || settings == null) return;
            try
            {
                var state = new {
                    processId = Process.GetCurrentProcess().Id, version = "0.6.0", interfaceMode = "codex-tray-only", panelVisible = Visible,
                    menuItems = menu == null ? new string[0] : menu.Items.OfType<ToolStripMenuItem>().Select(item => item.Text).ToArray(),
                    discoverySource = CodexLocator.DiscoverySource, registrationStatus = CodexLocator.RegistrationStatus,
                    isElevated = new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent()).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator),
                    writtenAt = DateTimeOffset.Now.ToString("o"), attempts = attempts, busy = busy, refreshElapsedMs = refreshElapsedMs,
                    stage = client.Stage, executable = client.ResolvedExecutable, queryProcessId = client.ChildProcessId,
                    home = settings.CodexHome, failure = failure, source = card.Snapshot == null ? null : card.Snapshot.Source,
                    observedAt = card.Snapshot == null ? null : card.Snapshot.RecordedAt.ToString("o"),
                    weeklyRemaining = card.Snapshot == null || card.Snapshot.Weekly == null ? (double?)null : card.Snapshot.Weekly.Remaining,
                    fiveHourRemaining = card.Snapshot == null || card.Snapshot.FiveHour == null ? (double?)null : card.Snapshot.FiveHour.Remaining
                };
                Directory.CreateDirectory(store.DirectoryPath);
                File.WriteAllText(Path.Combine(store.DirectoryPath, "runtime-status.json"), new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(state), System.Text.Encoding.UTF8);
            }
            catch { }
        }
        public void RenderPreview(string output)
        {
            Opacity = 0; Show(); Application.DoEvents(); content.CreateControl(); PerformLayout();
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output)));
            using (Bitmap bitmap = new Bitmap(content.Width, content.Height))
            { content.DrawToBitmap(bitmap, content.ClientRectangle); bitmap.Save(output, ImageFormat.Png); }
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing && !resourcesDisposed)
            {
                resourcesDisposed = true; timer.Stop(); timer.Dispose(); client.Dispose();
                if (tray != null) { tray.Visible = false; tray.Dispose(); }
                if (menu != null) menu.Dispose(); if (currentIcon != null) currentIcon.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
