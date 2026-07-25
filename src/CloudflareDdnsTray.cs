using Microsoft.Win32;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

[assembly: System.Reflection.AssemblyTitle("Cloudflare DDNS")]
[assembly: System.Reflection.AssemblyProduct("Cloudflare DDNS")]
[assembly: System.Reflection.AssemblyCompany("宽宽")]
[assembly: System.Reflection.AssemblyCopyright("Copyright © 宽宽")]

namespace KuanKuan.CloudflareDdns
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new TrayApp());
        }
    }

    static class UiTheme
    {
        public static readonly Color Background = Color.FromArgb(245, 247, 251);
        public static readonly Color Surface = Color.White;
        public static readonly Color Accent = Color.FromArgb(243, 128, 32);
        public static readonly Color AccentDark = Color.FromArgb(207, 92, 16);
        public static readonly Color AccentSoft = Color.FromArgb(255, 242, 229);
        public static readonly Color Text = Color.FromArgb(24, 32, 51);
        public static readonly Color Muted = Color.FromArgb(102, 112, 133);
        public static readonly Color Border = Color.FromArgb(222, 226, 234);
        public static readonly Color Success = Color.FromArgb(22, 163, 74);
        public static readonly Color Error = Color.FromArgb(220, 38, 38);

        public static Button CreateButton(string text, bool primary)
        {
            var button = new Button();
            button.Text = text;
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderSize = primary ? 0 : 1;
            button.FlatAppearance.BorderColor = primary ? Accent : Border;
            button.BackColor = primary ? Accent : Surface;
            button.ForeColor = primary ? Color.White : Text;
            button.Cursor = Cursors.Hand;
            button.Font = new Font("Segoe UI", 9.5f, FontStyle.Bold);
            button.Size = new Size(126, 38);
            button.UseVisualStyleBackColor = false;
            return button;
        }

        public static Label CreateLabel(string text, float size, FontStyle style, Color color)
        {
            return new Label
            {
                AutoSize = false,
                Text = text,
                Font = new Font("Segoe UI", size, style),
                ForeColor = color,
                BackColor = Color.Transparent
            };
        }
    }

    sealed class AppConfig
    {
        public string ZoneName = "example.com";
        public string RecordNames = "home.example.com";
        public string ApiEmail = "";
        public string ApiToken = "";
        public string ApiTokenProtected = "";
        public string GlobalApiKeyProtected = "";
        public bool UseGlobalApiKey = true;
        public string TargetIp = "";
        public string IpServices = "https://ipv4.icanhazip.com,https://checkip.amazonaws.com,https://api.ipify.org";
        public int Ttl = 120;
        public bool Proxied = false;
        public bool RefreshOnStart = true;
        public bool StartWithWindows = true;
    }

    sealed class TrayApp : ApplicationContext
    {
        const int AutoRefreshMinutes = 30;
        const int AutoRefreshInterval = AutoRefreshMinutes * 60 * 1000;

        readonly string appDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "KuanKuanCloudflareDDNS");
        readonly string configPath;
        readonly NotifyIcon tray;
        readonly ToolStripMenuItem statusItem;
        readonly ToolStripMenuItem ipItem;
        readonly ToolStripMenuItem timeItem;
        readonly ToolStripMenuItem nextItem;
        readonly ToolStripMenuItem refreshItem;
        readonly Timer refreshTimer;
        Timer startupTimer;
        DashboardForm dashboard;
        AppConfig config;
        string lastIp = "尚未获取";
        string lastStatus = "就绪，等待同步";
        DateTime? lastRunAt;
        DateTime? nextRunAt;
        bool lastSucceeded = true;
        bool busy;

        public TrayApp()
        {
            Directory.CreateDirectory(appDir);
            configPath = Path.Combine(appDir, "config.json");
            config = LoadConfig();

            tray = new NotifyIcon();
            tray.Icon = LoadIcon();
            tray.Text = "Cloudflare DDNS";
            tray.Visible = true;

            var menu = new ContextMenuStrip();
            menu.Font = new Font("Segoe UI", 9.5f);
            var openItem = new ToolStripMenuItem("打开状态面板", null, (s, e) => ShowDashboard());
            openItem.Font = new Font(menu.Font, FontStyle.Bold);
            menu.Items.Add(openItem);
            menu.Items.Add(new ToolStripSeparator());
            statusItem = AddDisabled(menu, "● 就绪");
            ipItem = AddDisabled(menu, "当前 IP：尚未获取");
            timeItem = AddDisabled(menu, "上次同步：尚未执行");
            nextItem = AddDisabled(menu, "下次同步：计划中");
            menu.Items.Add(new ToolStripSeparator());
            refreshItem = new ToolStripMenuItem("立即同步", null, async (s, e) => await RefreshAsync(true));
            menu.Items.Add(refreshItem);
            menu.Items.Add(new ToolStripMenuItem("设置", null, (s, e) => ShowSettings()));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem("退出", null, (s, e) => Exit()));
            tray.ContextMenuStrip = menu;
            tray.DoubleClick += (s, e) => ShowDashboard();

            refreshTimer = new Timer();
            refreshTimer.Interval = AutoRefreshInterval;
            refreshTimer.Tick += async (s, e) => await RefreshAsync(false);
            ScheduleNextRefresh();

            ApplyStartup(config.StartWithWindows);
            UpdateUi();

            if (config.RefreshOnStart)
            {
                startupTimer = new Timer();
                startupTimer.Interval = 600;
                startupTimer.Tick += async (s, e) =>
                {
                    startupTimer.Stop();
                    startupTimer.Dispose();
                    startupTimer = null;
                    await RefreshAsync(false);
                };
                startupTimer.Start();
            }
        }

        ToolStripMenuItem AddDisabled(ContextMenuStrip menu, string text)
        {
            var item = new ToolStripMenuItem(text) { Enabled = false };
            menu.Items.Add(item);
            return item;
        }

        Icon LoadIcon()
        {
            var localIcon = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "CloudflareDDNS.ico");
            if (File.Exists(localIcon)) return new Icon(localIcon);
            return SystemIcons.Application;
        }

        AppConfig LoadConfig()
        {
            if (!File.Exists(configPath))
            {
                var c = new AppConfig();
                var oldKey = Environment.GetEnvironmentVariable("CF_GLOBAL_API_KEY", EnvironmentVariableTarget.User);
                if (!string.IsNullOrWhiteSpace(oldKey)) c.GlobalApiKeyProtected = Protect(oldKey);
                SaveConfig(c);
                return c;
            }
            try
            {
                var serializer = new JavaScriptSerializer();
                return serializer.Deserialize<AppConfig>(File.ReadAllText(configPath, Encoding.UTF8)) ?? new AppConfig();
            }
            catch
            {
                return new AppConfig();
            }
        }

        void SaveConfig(AppConfig c)
        {
            var serializer = new JavaScriptSerializer();
            File.WriteAllText(configPath, serializer.Serialize(c), Encoding.UTF8);
        }

        static string Protect(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            var bytes = Encoding.UTF8.GetBytes(value);
            var protectedBytes = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(protectedBytes);
        }

        static string Unprotect(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "";
            try
            {
                var bytes = Convert.FromBase64String(value);
                return Encoding.UTF8.GetString(ProtectedData.Unprotect(bytes, null, DataProtectionScope.CurrentUser));
            }
            catch
            {
                return "";
            }
        }

        async Task RefreshAsync(bool manual)
        {
            if (busy) return;
            busy = true;
            refreshTimer.Stop();
            nextRunAt = null;
            refreshItem.Enabled = false;
            lastStatus = "正在检测公网 IP 并同步 DNS…";
            lastSucceeded = true;
            UpdateUi();

            try
            {
                var result = await Task.Run(() => DdnsClient.Refresh(config));
                lastIp = result.Ip;
                lastRunAt = DateTime.Now;
                lastStatus = result.Message;
                lastSucceeded = true;
                if (manual) ShowNotification("同步完成", result.Message, ToolTipIcon.Info);
            }
            catch (Exception ex)
            {
                lastRunAt = DateTime.Now;
                lastStatus = "同步失败：" + Shorten(ex.Message, 180);
                lastSucceeded = false;
                if (manual) ShowNotification("同步失败", Shorten(ex.Message, 180), ToolTipIcon.Error);
            }
            finally
            {
                busy = false;
                refreshItem.Enabled = true;
                ScheduleNextRefresh();
                UpdateUi();
            }
        }

        void ScheduleNextRefresh()
        {
            nextRunAt = DateTime.Now.AddMinutes(AutoRefreshMinutes);
            refreshTimer.Stop();
            refreshTimer.Interval = AutoRefreshInterval;
            refreshTimer.Start();
        }

        void ShowNotification(string title, string message, ToolTipIcon icon)
        {
            tray.BalloonTipTitle = title;
            tray.BalloonTipText = Shorten(message, 220);
            tray.BalloonTipIcon = icon;
            tray.ShowBalloonTip(3500);
        }

        void UpdateUi()
        {
            if (tray.ContextMenuStrip.InvokeRequired)
            {
                tray.ContextMenuStrip.BeginInvoke(new Action(UpdateUi));
                return;
            }

            statusItem.Text = (lastSucceeded ? "● " : "● ") + Shorten(lastStatus, 90);
            statusItem.ForeColor = lastSucceeded ? UiTheme.Success : UiTheme.Error;
            ipItem.Text = "当前 IP：" + lastIp;
            timeItem.Text = "上次同步：" + FormatTime(lastRunAt, "尚未执行");
            nextItem.Text = busy
                ? "下次同步：本次完成后 30 分钟"
                : "下次同步：" + FormatTime(nextRunAt, "计划中");

            var tip = "Cloudflare DDNS | " + lastIp + " | " + (busy ? "同步中" : Shorten(lastStatus, 28));
            tray.Text = Shorten(tip, 63);

            if (dashboard != null && !dashboard.IsDisposed)
            {
                dashboard.UpdateSnapshot(
                    lastIp,
                    lastRunAt,
                    nextRunAt,
                    lastStatus,
                    lastSucceeded,
                    busy,
                    config.RecordNames,
                    AutoRefreshMinutes);
            }
        }

        static string FormatTime(DateTime? value, string empty)
        {
            return value.HasValue ? value.Value.ToString("yyyy-MM-dd HH:mm:ss") : empty;
        }

        static string Shorten(string text, int max)
        {
            if (string.IsNullOrEmpty(text) || text.Length <= max) return text;
            return text.Substring(0, max - 3) + "...";
        }

        void ShowDashboard()
        {
            if (dashboard == null || dashboard.IsDisposed)
            {
                dashboard = new DashboardForm(LoadIcon(), async () => await RefreshAsync(true), ShowSettings);
            }

            UpdateUi();
            if (!dashboard.Visible) dashboard.Show();
            if (dashboard.WindowState == FormWindowState.Minimized) dashboard.WindowState = FormWindowState.Normal;
            dashboard.BringToFront();
            dashboard.Activate();
        }

        void ShowSettings()
        {
            var secretValue = config.UseGlobalApiKey ? Unprotect(config.GlobalApiKeyProtected) : Unprotect(config.ApiTokenProtected);
            using (var form = new SettingsForm(config, secretValue, LoadIcon()))
            {
                var result = dashboard != null && dashboard.Visible
                    ? form.ShowDialog(dashboard)
                    : form.ShowDialog();
                if (result == DialogResult.OK)
                {
                    var oldToken = config.ApiTokenProtected;
                    var oldGlobalKey = config.GlobalApiKeyProtected;
                    config = form.Result;
                    config.ApiTokenProtected = oldToken;
                    config.GlobalApiKeyProtected = oldGlobalKey;
                    if (config.UseGlobalApiKey)
                        config.GlobalApiKeyProtected = Protect(form.SecretValue);
                    else
                        config.ApiTokenProtected = Protect(form.SecretValue);
                    SaveConfig(config);
                    ApplyStartup(config.StartWithWindows);
                    lastStatus = "设置已保存，自动同步周期为 30 分钟";
                    lastSucceeded = true;
                    ScheduleNextRefresh();
                    UpdateUi();
                }
            }
        }

        void ApplyStartup(bool enabled)
        {
            using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true))
            {
                if (key == null) return;
                const string name = "KuanKuan Cloudflare DDNS";
                if (enabled)
                    key.SetValue(name, "\"" + Application.ExecutablePath + "\"");
                else
                    key.DeleteValue(name, false);
            }
        }

        void Exit()
        {
            refreshTimer.Stop();
            refreshTimer.Dispose();
            if (startupTimer != null) startupTimer.Dispose();
            if (dashboard != null && !dashboard.IsDisposed)
            {
                dashboard.AllowClose = true;
                dashboard.Close();
            }
            tray.Visible = false;
            tray.Dispose();
            Application.Exit();
        }
    }

    sealed class DashboardForm : Form
    {
        readonly Label statusLabel;
        readonly Label ipValue;
        readonly Label lastValue;
        readonly Label nextValue;
        readonly Label recordLabel;
        readonly Label scheduleLabel;
        readonly Button refreshButton;
        readonly Func<Task> refreshAction;
        readonly Action settingsAction;

        public bool AllowClose { get; set; }

        public DashboardForm(Icon icon, Func<Task> refresh, Action settings)
        {
            refreshAction = refresh;
            settingsAction = settings;
            Icon = icon;
            Text = "Cloudflare DDNS";
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            AutoScaleDimensions = new SizeF(96f, 96f);
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(720, 500);
            BackColor = UiTheme.Background;
            Font = new Font("Segoe UI", 9f);

            var header = new Panel
            {
                Dock = DockStyle.Top,
                Height = 116,
                BackColor = UiTheme.Accent
            };
            Controls.Add(header);

            var logo = new PictureBox
            {
                Image = icon.ToBitmap(),
                Location = new Point(28, 28),
                Size = new Size(56, 56),
                SizeMode = PictureBoxSizeMode.Zoom,
                BackColor = Color.Transparent
            };
            header.Controls.Add(logo);

            var title = UiTheme.CreateLabel("Cloudflare DDNS", 22f, FontStyle.Bold, Color.White);
            title.Location = new Point(102, 24);
            title.Size = new Size(430, 36);
            header.Controls.Add(title);

            var subtitle = UiTheme.CreateLabel("稳定守护你的公网地址 · 每 30 分钟自动同步", 10f, FontStyle.Regular, Color.FromArgb(255, 244, 235));
            subtitle.Location = new Point(104, 64);
            subtitle.Size = new Size(500, 26);
            header.Controls.Add(subtitle);

            var statusCard = CreateCard(new Point(24, 136), new Size(672, 88));
            Controls.Add(statusCard);
            var statusTitle = UiTheme.CreateLabel("运行状态", 9f, FontStyle.Bold, UiTheme.Muted);
            statusTitle.Location = new Point(18, 13);
            statusTitle.Size = new Size(100, 22);
            statusCard.Controls.Add(statusTitle);
            statusLabel = UiTheme.CreateLabel("就绪，等待同步", 11f, FontStyle.Bold, UiTheme.Success);
            statusLabel.Location = new Point(18, 39);
            statusLabel.Size = new Size(630, 32);
            statusCard.Controls.Add(statusLabel);

            var metricY = 242;
            var metricWidth = 216;
            var ipCard = CreateMetricCard("当前公网 IP", "尚未获取", new Point(24, metricY), metricWidth, out ipValue);
            var lastCard = CreateMetricCard("上次同步", "尚未执行", new Point(252, metricY), metricWidth, out lastValue);
            var nextCard = CreateMetricCard("下次同步", "计划中", new Point(480, metricY), metricWidth, out nextValue);
            Controls.Add(ipCard);
            Controls.Add(lastCard);
            Controls.Add(nextCard);

            refreshButton = UiTheme.CreateButton("立即同步", true);
            refreshButton.Location = new Point(24, 356);
            refreshButton.Click += async (s, e) => await refreshAction();
            Controls.Add(refreshButton);

            var settingsButton = UiTheme.CreateButton("设置", false);
            settingsButton.Location = new Point(162, 356);
            settingsButton.Click += (s, e) => settingsAction();
            Controls.Add(settingsButton);

            recordLabel = UiTheme.CreateLabel("记录：-", 9f, FontStyle.Regular, UiTheme.Text);
            recordLabel.Location = new Point(24, 414);
            recordLabel.Size = new Size(650, 24);
            Controls.Add(recordLabel);

            scheduleLabel = UiTheme.CreateLabel("自动同步周期：30 分钟", 9f, FontStyle.Regular, UiTheme.Muted);
            scheduleLabel.Location = new Point(24, 442);
            scheduleLabel.Size = new Size(330, 24);
            Controls.Add(scheduleLabel);

            var trayHint = UiTheme.CreateLabel("关闭此窗口后程序仍会在托盘后台运行", 9f, FontStyle.Regular, UiTheme.Muted);
            trayHint.TextAlign = ContentAlignment.MiddleRight;
            trayHint.Location = new Point(355, 442);
            trayHint.Size = new Size(341, 24);
            Controls.Add(trayHint);

            FormClosing += (s, e) =>
            {
                if (!AllowClose && e.CloseReason == CloseReason.UserClosing)
                {
                    e.Cancel = true;
                    Hide();
                }
            };
        }

        static Panel CreateCard(Point location, Size size)
        {
            return new Panel
            {
                Location = location,
                Size = size,
                BackColor = UiTheme.Surface,
                BorderStyle = BorderStyle.FixedSingle
            };
        }

        static Panel CreateMetricCard(string title, string value, Point location, int width, out Label valueLabel)
        {
            var card = CreateCard(location, new Size(width, 94));
            var titleLabel = UiTheme.CreateLabel(title, 9f, FontStyle.Regular, UiTheme.Muted);
            titleLabel.Location = new Point(16, 14);
            titleLabel.Size = new Size(width - 32, 22);
            card.Controls.Add(titleLabel);
            valueLabel = UiTheme.CreateLabel(value, 12f, FontStyle.Bold, UiTheme.Text);
            valueLabel.Location = new Point(16, 44);
            valueLabel.Size = new Size(width - 32, 30);
            card.Controls.Add(valueLabel);
            return card;
        }

        public void UpdateSnapshot(string ip, DateTime? lastRun, DateTime? nextRun, string status, bool success, bool isBusy, string records, int intervalMinutes)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action<string, DateTime?, DateTime?, string, bool, bool, string, int>(UpdateSnapshot),
                    ip, lastRun, nextRun, status, success, isBusy, records, intervalMinutes);
                return;
            }

            statusLabel.Text = status;
            statusLabel.ForeColor = isBusy ? UiTheme.AccentDark : (success ? UiTheme.Success : UiTheme.Error);
            ipValue.Text = ip;
            lastValue.Text = lastRun.HasValue ? lastRun.Value.ToString("MM-dd HH:mm:ss") : "尚未执行";
            nextValue.Text = isBusy ? "同步完成后计时" : (nextRun.HasValue ? nextRun.Value.ToString("MM-dd HH:mm:ss") : "计划中");
            recordLabel.Text = "DNS 记录：" + (string.IsNullOrWhiteSpace(records) ? "未设置" : records);
            scheduleLabel.Text = "自动同步周期：" + intervalMinutes + " 分钟";
            refreshButton.Enabled = !isBusy;
            refreshButton.Text = isBusy ? "正在同步…" : "立即同步";
            UseWaitCursor = isBusy;
        }
    }

    sealed class SettingsForm : Form
    {
        readonly TextBox zone = new TextBox();
        readonly TextBox records = new TextBox();
        readonly TextBox email = new TextBox();
        readonly TextBox secret = new TextBox();
        readonly TextBox targetIp = new TextBox();
        readonly TextBox ipServices = new TextBox();
        readonly NumericUpDown ttl = new NumericUpDown();
        readonly ComboBox authMode = new ComboBox();
        readonly CheckBox showSecret = new CheckBox();
        readonly CheckBox proxied = new CheckBox();
        readonly CheckBox refreshOnStart = new CheckBox();
        readonly CheckBox startWithWindows = new CheckBox();
        Label emailLabel;
        public AppConfig Result { get; private set; }
        public string SecretValue { get; private set; }

        public SettingsForm(AppConfig config, string secretValue, Icon icon)
        {
            Result = config;
            SecretValue = secretValue;
            Icon = icon;
            Text = "Cloudflare DDNS 设置";
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            AutoScaleDimensions = new SizeF(96f, 96f);
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(720, 650);
            BackColor = UiTheme.Background;
            Font = new Font("Segoe UI", 9f);

            var header = new Panel { Dock = DockStyle.Top, Height = 88, BackColor = UiTheme.Surface };
            Controls.Add(header);
            var title = UiTheme.CreateLabel("设置", 20f, FontStyle.Bold, UiTheme.Text);
            title.Location = new Point(24, 16);
            title.Size = new Size(200, 34);
            header.Controls.Add(title);
            var subtitle = UiTheme.CreateLabel("配置 Cloudflare 记录、认证方式与自动启动选项", 9.5f, FontStyle.Regular, UiTheme.Muted);
            subtitle.Location = new Point(26, 52);
            subtitle.Size = new Size(520, 24);
            header.Controls.Add(subtitle);

            var tabs = new TabControl
            {
                Location = new Point(24, 106),
                Size = new Size(672, 466),
                Font = new Font("Segoe UI", 9.5f),
                Padding = new Point(18, 7)
            };
            Controls.Add(tabs);

            var dnsPage = new TabPage("DNS 与认证") { BackColor = UiTheme.Surface };
            var automationPage = new TabPage("自动同步") { BackColor = UiTheme.Surface };
            tabs.TabPages.Add(dnsPage);
            tabs.TabPages.Add(automationPage);
            BuildDnsPage(dnsPage);
            BuildAutomationPage(automationPage);

            var save = UiTheme.CreateButton("保存设置", true);
            save.Location = new Point(432, 592);
            save.Click += (s, e) => Save();
            Controls.Add(save);

            var cancel = UiTheme.CreateButton("取消", false);
            cancel.Location = new Point(570, 592);
            cancel.Click += (s, e) => DialogResult = DialogResult.Cancel;
            Controls.Add(cancel);
            AcceptButton = save;
            CancelButton = cancel;

            zone.Text = config.ZoneName;
            records.Text = config.RecordNames;
            email.Text = config.ApiEmail;
            secret.Text = secretValue;
            secret.UseSystemPasswordChar = true;
            targetIp.Text = config.TargetIp;
            ipServices.Text = config.IpServices;
            ttl.Value = Math.Max(ttl.Minimum, Math.Min(ttl.Maximum, config.Ttl));
            authMode.SelectedIndex = config.UseGlobalApiKey ? 1 : 0;
            proxied.Checked = config.Proxied;
            refreshOnStart.Checked = config.RefreshOnStart;
            startWithWindows.Checked = config.StartWithWindows;
            UpdateAuthFields();
        }

        void BuildDnsPage(TabPage page)
        {
            int y = 22;
            StyleInput(zone);
            StyleInput(records);
            StyleInput(email);
            StyleInput(secret);
            StyleInput(targetIp);

            AddRow(page, "Zone 区域", "例如 example.com", zone, ref y, null);
            AddRow(page, "A 记录", "多个记录请用英文逗号分隔", records, ref y, null);

            authMode.DropDownStyle = ComboBoxStyle.DropDownList;
            authMode.Items.Add("API Token（推荐）");
            authMode.Items.Add("Global API Key");
            authMode.SelectedIndexChanged += (s, e) => UpdateAuthFields();
            AddRow(page, "认证方式", "推荐使用最小权限的 API Token", authMode, ref y, null);

            AddRow(page, "账号邮箱", "仅 Global API Key 模式需要", email, ref y, label => emailLabel = label);

            var secretPanel = new Panel { BackColor = Color.Transparent };
            secret.Location = new Point(0, 0);
            secret.Size = new Size(320, 27);
            secretPanel.Controls.Add(secret);
            showSecret.Text = "显示";
            showSecret.Location = new Point(334, 2);
            showSecret.Size = new Size(70, 25);
            showSecret.CheckedChanged += (s, e) => secret.UseSystemPasswordChar = !showSecret.Checked;
            secretPanel.Controls.Add(showSecret);
            AddRow(page, "API 密钥 / Token", "密钥使用 Windows DPAPI 加密保存", secretPanel, ref y, null);

            AddRow(page, "固定 IPv4", "留空时自动检测公网 IPv4", targetIp, ref y, null);

            ttl.Minimum = 60;
            ttl.Maximum = 86400;
            ttl.Increment = 60;
            ttl.Width = 160;
            AddRow(page, "TTL（秒）", "Cloudflare 代理开启时会自动使用其规则", ttl, ref y, null);

            proxied.Text = "开启 Cloudflare 代理（橙云）";
            proxied.Location = new Point(178, y + 2);
            proxied.Size = new Size(250, 28);
            proxied.ForeColor = UiTheme.Text;
            page.Controls.Add(proxied);
        }

        void BuildAutomationPage(TabPage page)
        {
            var intervalCard = new Panel
            {
                Location = new Point(24, 24),
                Size = new Size(608, 94),
                BackColor = UiTheme.AccentSoft,
                BorderStyle = BorderStyle.FixedSingle
            };
            page.Controls.Add(intervalCard);
            var intervalTitle = UiTheme.CreateLabel("自动同步周期", 9.5f, FontStyle.Bold, UiTheme.AccentDark);
            intervalTitle.Location = new Point(18, 14);
            intervalTitle.Size = new Size(160, 24);
            intervalCard.Controls.Add(intervalTitle);
            var intervalValue = UiTheme.CreateLabel("30 分钟", 20f, FontStyle.Bold, UiTheme.Text);
            intervalValue.Location = new Point(18, 43);
            intervalValue.Size = new Size(180, 36);
            intervalCard.Controls.Add(intervalValue);
            var intervalHint = UiTheme.CreateLabel("每次自动或手动同步完成后重新计时", 9f, FontStyle.Regular, UiTheme.Muted);
            intervalHint.TextAlign = ContentAlignment.MiddleRight;
            intervalHint.Location = new Point(260, 44);
            intervalHint.Size = new Size(320, 30);
            intervalCard.Controls.Add(intervalHint);

            refreshOnStart.Text = "程序启动时立即同步一次";
            refreshOnStart.Location = new Point(28, 142);
            refreshOnStart.Size = new Size(260, 28);
            refreshOnStart.ForeColor = UiTheme.Text;
            page.Controls.Add(refreshOnStart);

            startWithWindows.Text = "随 Windows 自动启动";
            startWithWindows.Location = new Point(316, 142);
            startWithWindows.Size = new Size(240, 28);
            startWithWindows.ForeColor = UiTheme.Text;
            page.Controls.Add(startWithWindows);

            var servicesTitle = UiTheme.CreateLabel("公网 IPv4 检测服务", 10f, FontStyle.Bold, UiTheme.Text);
            servicesTitle.Location = new Point(28, 196);
            servicesTitle.Size = new Size(240, 25);
            page.Controls.Add(servicesTitle);
            var servicesHint = UiTheme.CreateLabel("按顺序尝试，多个地址请用英文逗号分隔；检测请求不使用系统代理。", 8.8f, FontStyle.Regular, UiTheme.Muted);
            servicesHint.Location = new Point(28, 224);
            servicesHint.Size = new Size(570, 24);
            page.Controls.Add(servicesHint);
            ipServices.Multiline = true;
            ipServices.ScrollBars = ScrollBars.Vertical;
            ipServices.Location = new Point(28, 256);
            ipServices.Size = new Size(574, 88);
            ipServices.Font = new Font("Consolas", 9f);
            ipServices.BorderStyle = BorderStyle.FixedSingle;
            page.Controls.Add(ipServices);

            var note = UiTheme.CreateLabel("提示：关闭状态面板不会退出程序，自动同步会继续在系统托盘中运行。", 9f, FontStyle.Regular, UiTheme.Muted);
            note.Location = new Point(28, 370);
            note.Size = new Size(580, 28);
            page.Controls.Add(note);
        }

        static void StyleInput(TextBox textBox)
        {
            textBox.BorderStyle = BorderStyle.FixedSingle;
            textBox.Font = new Font("Segoe UI", 9.5f);
        }

        void AddRow(TabPage page, string title, string hint, Control control, ref int y, Action<Label> captureLabel)
        {
            var label = UiTheme.CreateLabel(title, 9.5f, FontStyle.Bold, UiTheme.Text);
            label.Location = new Point(24, y + 3);
            label.Size = new Size(145, 24);
            page.Controls.Add(label);
            if (captureLabel != null) captureLabel(label);

            var hintLabel = UiTheme.CreateLabel(hint, 8.2f, FontStyle.Regular, UiTheme.Muted);
            hintLabel.Location = new Point(24, y + 26);
            hintLabel.Size = new Size(145, 30);
            page.Controls.Add(hintLabel);

            control.Location = new Point(178, y);
            control.Size = new Size(430, 28);
            page.Controls.Add(control);
            y += 55;
        }

        void UpdateAuthFields()
        {
            var useGlobal = authMode.SelectedIndex == 1;
            email.Enabled = useGlobal;
            if (emailLabel != null) emailLabel.ForeColor = useGlobal ? UiTheme.Text : UiTheme.Muted;
        }

        void Save()
        {
            if (string.IsNullOrWhiteSpace(zone.Text) || string.IsNullOrWhiteSpace(records.Text))
            {
                MessageBox.Show("请填写 Zone 区域和至少一个 A 记录。", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (authMode.SelectedIndex == 1 && string.IsNullOrWhiteSpace(email.Text))
            {
                MessageBox.Show("使用 Global API Key 时必须填写账号邮箱。", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (string.IsNullOrWhiteSpace(secret.Text))
            {
                MessageBox.Show("请填写 API Token 或 Global API Key。", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (!string.IsNullOrWhiteSpace(targetIp.Text) && !DdnsClient.IsIPv4(targetIp.Text.Trim()))
            {
                MessageBox.Show("固定 IPv4 地址格式不正确。", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (string.IsNullOrWhiteSpace(ipServices.Text) && string.IsNullOrWhiteSpace(targetIp.Text))
            {
                MessageBox.Show("未设置固定 IPv4 时，至少需要一个公网 IP 检测服务。", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            Result = new AppConfig
            {
                ZoneName = zone.Text.Trim().TrimEnd('.'),
                RecordNames = records.Text.Trim(),
                ApiEmail = email.Text.Trim(),
                UseGlobalApiKey = authMode.SelectedIndex == 1,
                TargetIp = targetIp.Text.Trim(),
                IpServices = ipServices.Text.Trim(),
                Ttl = (int)ttl.Value,
                Proxied = proxied.Checked,
                RefreshOnStart = refreshOnStart.Checked,
                StartWithWindows = startWithWindows.Checked
            };
            SecretValue = secret.Text.Trim();
            DialogResult = DialogResult.OK;
        }
    }

    sealed class RefreshResult
    {
        public string Ip;
        public string Message;
    }

    static class DdnsClient
    {
        static readonly Regex Ipv4Regex = new Regex(@"^(?:(?:25[0-5]|2[0-4]\d|1?\d?\d)\.){3}(?:25[0-5]|2[0-4]\d|1?\d?\d)$");
        static readonly JavaScriptSerializer Json = new JavaScriptSerializer();

        public static bool IsIPv4(string value) { return Ipv4Regex.IsMatch(value ?? ""); }

        public static RefreshResult Refresh(AppConfig config)
        {
            var ip = string.IsNullOrWhiteSpace(config.TargetIp) ? DetectIp(config.IpServices) : config.TargetIp.Trim();
            if (!IsIPv4(ip)) throw new InvalidOperationException("IPv4 地址无效：" + ip);
            var headers = BuildHeaders(config);
            var zone = ApiGet(headers, "/zones?name=" + Uri.EscapeDataString(config.ZoneName) + "&status=active");
            var zoneList = zone["result"] as IList;
            if (zoneList == null || zoneList.Count == 0) throw new InvalidOperationException("找不到 Zone：" + config.ZoneName);
            var zoneId = (string)((Dictionary<string, object>)zoneList[0])["id"];

            var messages = new List<string>();
            foreach (var rawName in config.RecordNames.Split(','))
            {
                var name = rawName.Trim().TrimEnd('.');
                if (name.Length == 0) continue;
                var dns = ApiGet(headers, "/zones/" + zoneId + "/dns_records?type=A&name=" + Uri.EscapeDataString(name));
                var records = dns["result"] as IList;
                if (records == null || records.Count == 0)
                {
                    var body = new Dictionary<string, object> {
                        {"type", "A"}, {"name", name}, {"content", ip}, {"ttl", config.Ttl}, {"proxied", config.Proxied}
                    };
                    ApiSend(headers, "POST", "/zones/" + zoneId + "/dns_records", body);
                    messages.Add("已创建 " + name + " → " + ip);
                }
                else
                {
                    var record = (Dictionary<string, object>)records[0];
                    var current = Convert.ToString(record["content"]);
                    var proxied = Convert.ToBoolean(record["proxied"]);
                    var ttl = Convert.ToInt32(record["ttl"]);
                    if (current == ip && proxied == config.Proxied && ttl == config.Ttl)
                    {
                        messages.Add(name + " 无需更改");
                    }
                    else
                    {
                        var body = new Dictionary<string, object> {
                            {"content", ip}, {"ttl", config.Ttl}, {"proxied", config.Proxied}
                        };
                        ApiSend(headers, "PATCH", "/zones/" + zoneId + "/dns_records/" + record["id"], body);
                        messages.Add("已更新 " + name + "：" + current + " → " + ip);
                    }
                }
            }

            if (messages.Count == 0) throw new InvalidOperationException("没有可同步的 A 记录。");
            return new RefreshResult { Ip = ip, Message = string.Join("；", messages.ToArray()) };
        }

        static Dictionary<string, string> BuildHeaders(AppConfig config)
        {
            var headers = new Dictionary<string, string>();
            if (config.UseGlobalApiKey)
            {
                if (string.IsNullOrWhiteSpace(config.ApiEmail)) throw new InvalidOperationException("Global API Key 模式需要账号邮箱。");
                var key = GetSecret(config);
                if (string.IsNullOrWhiteSpace(key)) throw new InvalidOperationException("请先设置 Global API Key。");
                headers["X-Auth-Email"] = config.ApiEmail;
                headers["X-Auth-Key"] = key;
            }
            else
            {
                var token = GetApiToken(config);
                if (string.IsNullOrWhiteSpace(token)) throw new InvalidOperationException("请先设置 API Token。");
                headers["Authorization"] = "Bearer " + token;
            }
            return headers;
        }

        static string GetSecret(AppConfig config)
        {
            if (string.IsNullOrWhiteSpace(config.GlobalApiKeyProtected)) return "";
            try
            {
                var bytes = Convert.FromBase64String(config.GlobalApiKeyProtected);
                return Encoding.UTF8.GetString(ProtectedData.Unprotect(bytes, null, DataProtectionScope.CurrentUser));
            }
            catch { return ""; }
        }

        static string GetApiToken(AppConfig config)
        {
            if (!string.IsNullOrWhiteSpace(config.ApiTokenProtected))
            {
                try
                {
                    var bytes = Convert.FromBase64String(config.ApiTokenProtected);
                    return Encoding.UTF8.GetString(ProtectedData.Unprotect(bytes, null, DataProtectionScope.CurrentUser));
                }
                catch { }
            }
            return config.ApiToken ?? "";
        }

        static string DetectIp(string services)
        {
            var errors = new List<string>();
            foreach (var service in (services ?? "").Split(',').Select(s => s.Trim()).Where(s => s.Length > 0))
            {
                try
                {
                    var req = (HttpWebRequest)WebRequest.Create(service);
                    req.Proxy = null;
                    req.Timeout = 15000;
                    using (var resp = req.GetResponse())
                    using (var stream = resp.GetResponseStream())
                    using (var reader = new StreamReader(stream))
                    {
                        var value = reader.ReadToEnd().Trim();
                        if (IsIPv4(value)) return value;
                        errors.Add(service + " 返回 " + value);
                    }
                }
                catch (Exception ex)
                {
                    errors.Add(service + " 失败：" + ex.Message);
                }
            }
            throw new InvalidOperationException("无法检测公网 IPv4。" + string.Join("；", errors.ToArray()));
        }

        static Dictionary<string, object> ApiGet(Dictionary<string, string> headers, string path)
        {
            return ApiSend(headers, "GET", path, null);
        }

        static Dictionary<string, object> ApiSend(Dictionary<string, string> headers, string method, string path, Dictionary<string, object> body)
        {
            var req = (HttpWebRequest)WebRequest.Create("https://api.cloudflare.com/client/v4" + path);
            req.Method = method;
            req.Proxy = null;
            req.Timeout = 20000;
            foreach (var kv in headers) req.Headers[kv.Key] = kv.Value;
            req.ContentType = "application/json";
            if (body != null)
            {
                var bytes = Encoding.UTF8.GetBytes(Json.Serialize(body));
                req.ContentLength = bytes.Length;
                using (var stream = req.GetRequestStream()) stream.Write(bytes, 0, bytes.Length);
            }

            string text;
            try
            {
                using (var resp = req.GetResponse())
                using (var stream = resp.GetResponseStream())
                using (var reader = new StreamReader(stream))
                    text = reader.ReadToEnd();
            }
            catch (WebException ex)
            {
                if (ex.Response != null)
                {
                    using (var stream = ex.Response.GetResponseStream())
                    using (var reader = new StreamReader(stream))
                        throw new InvalidOperationException(ParseCfError(reader.ReadToEnd()));
                }
                throw;
            }

            var parsed = Json.Deserialize<Dictionary<string, object>>(text);
            if (!Convert.ToBoolean(parsed["success"])) throw new InvalidOperationException(ParseCfError(text));
            return parsed;
        }

        static string ParseCfError(string text)
        {
            try
            {
                var parsed = Json.Deserialize<Dictionary<string, object>>(text);
                var errors = parsed["errors"] as IList;
                if (errors != null && errors.Count > 0)
                {
                    var messages = new List<string>();
                    foreach (var error in errors)
                        messages.Add(Convert.ToString(((Dictionary<string, object>)error)["message"]));
                    return string.Join("；", messages.ToArray());
                }
            }
            catch { }
            return text;
        }
    }
}
