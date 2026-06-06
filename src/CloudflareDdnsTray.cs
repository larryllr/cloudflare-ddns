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
        readonly string appDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "KuanKuanCloudflareDDNS");
        readonly string configPath;
        readonly NotifyIcon tray;
        readonly ToolStripMenuItem statusItem;
        readonly ToolStripMenuItem ipItem;
        readonly ToolStripMenuItem timeItem;
        readonly ToolStripMenuItem refreshItem;
        AppConfig config;
        string lastIp = "unknown";
        string lastRun = "never";
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
            statusItem = AddDisabled(menu, "Ready");
            ipItem = AddDisabled(menu, "IP: unknown");
            timeItem = AddDisabled(menu, "Last: never");
            menu.Items.Add(new ToolStripSeparator());
            refreshItem = new ToolStripMenuItem("Refresh now", null, async (s, e) => await RefreshAsync(true));
            menu.Items.Add(refreshItem);
            menu.Items.Add(new ToolStripMenuItem("Settings", null, (s, e) => ShowSettings()));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem("Exit", null, (s, e) => Exit()));
            tray.ContextMenuStrip = menu;
            tray.DoubleClick += (s, e) => ShowSettings();

            ApplyStartup(config.StartWithWindows);
            UpdateStatus("Ready");
            if (config.RefreshOnStart)
            {
                Task.Run(async () => await RefreshAsync(false));
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
            refreshItem.Enabled = false;
            UpdateStatus("Refreshing...");

            try
            {
                var result = await Task.Run(() => DdnsClient.Refresh(config));
                lastIp = result.Ip;
                lastRun = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                UpdateStatus(result.Message);
            }
            catch (Exception ex)
            {
                lastRun = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                UpdateStatus("Failed: " + Shorten(ex.Message, 120));
            }
            finally
            {
                busy = false;
                refreshItem.Enabled = true;
            }
        }

        void UpdateStatus(string status)
        {
            if (tray.ContextMenuStrip.InvokeRequired)
            {
                tray.ContextMenuStrip.BeginInvoke(new Action<string>(UpdateStatus), status);
                return;
            }
            statusItem.Text = status;
            ipItem.Text = "IP: " + lastIp;
            timeItem.Text = "Last: " + lastRun;
            var tip = "Cloudflare DDNS\n" + ipItem.Text + "\n" + timeItem.Text + "\n" + status;
            tray.Text = Shorten(tip.Replace("\n", " | "), 63);
        }

        static string Shorten(string text, int max)
        {
            if (string.IsNullOrEmpty(text) || text.Length <= max) return text;
            return text.Substring(0, max - 3) + "...";
        }

        void ShowSettings()
        {
            var secretValue = config.UseGlobalApiKey ? Unprotect(config.GlobalApiKeyProtected) : Unprotect(config.ApiTokenProtected);
            using (var form = new SettingsForm(config, secretValue, LoadIcon()))
            {
                if (form.ShowDialog() == DialogResult.OK)
                {
                    config = form.Result;
                    if (config.UseGlobalApiKey)
                        config.GlobalApiKeyProtected = Protect(form.SecretValue);
                    else
                        config.ApiTokenProtected = Protect(form.SecretValue);
                    SaveConfig(config);
                    ApplyStartup(config.StartWithWindows);
                    UpdateStatus("Settings saved");
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
            tray.Visible = false;
            tray.Dispose();
            Application.Exit();
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
        readonly CheckBox useGlobal = new CheckBox();
        readonly CheckBox proxied = new CheckBox();
        readonly CheckBox refreshOnStart = new CheckBox();
        readonly CheckBox startWithWindows = new CheckBox();
        public AppConfig Result { get; private set; }
        public string SecretValue { get; private set; }

        public SettingsForm(AppConfig config, string secretValue, Icon icon)
        {
            Result = config;
            SecretValue = secretValue;
            Icon = icon;
            Text = "Cloudflare DDNS - 宽宽";
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ClientSize = new Size(560, 430);

            int y = 18;
            AddRow("Zone", zone, ref y);
            AddRow("Records", records, ref y);
            AddRow("Email", email, ref y);
            AddRow("API Key / Token", secret, ref y);
            AddRow("Custom IP", targetIp, ref y);
            AddRow("IP services", ipServices, ref y);

            ttl.Minimum = 60;
            ttl.Maximum = 86400;
            ttl.Increment = 60;
            AddRow("TTL", ttl, ref y);

            useGlobal.Text = "Use Global API Key";
            useGlobal.Location = new Point(135, y);
            useGlobal.Size = new Size(170, 24);
            Controls.Add(useGlobal);
            proxied.Text = "Cloudflare proxy";
            proxied.Location = new Point(320, y);
            proxied.Size = new Size(160, 24);
            Controls.Add(proxied);
            y += 34;

            refreshOnStart.Text = "Refresh when app starts";
            refreshOnStart.Location = new Point(135, y);
            refreshOnStart.Size = new Size(180, 24);
            Controls.Add(refreshOnStart);
            startWithWindows.Text = "Start with Windows";
            startWithWindows.Location = new Point(320, y);
            startWithWindows.Size = new Size(180, 24);
            Controls.Add(startWithWindows);

            var hint = new Label();
            hint.Text = "Custom IP empty = DDNS. Public IP detection bypasses system proxy.";
            hint.Location = new Point(135, y + 32);
            hint.Size = new Size(390, 24);
            Controls.Add(hint);

            var save = new Button { Text = "Save", Location = new Point(350, 385), Size = new Size(90, 30) };
            var cancel = new Button { Text = "Cancel", Location = new Point(450, 385), Size = new Size(90, 30) };
            Controls.Add(save);
            Controls.Add(cancel);
            cancel.Click += (s, e) => DialogResult = DialogResult.Cancel;
            save.Click += (s, e) => Save();

            zone.Text = config.ZoneName;
            records.Text = config.RecordNames;
            email.Text = config.ApiEmail;
            secret.Text = secretValue;
            secret.UseSystemPasswordChar = true;
            targetIp.Text = config.TargetIp;
            ipServices.Text = config.IpServices;
            ttl.Value = Math.Max(ttl.Minimum, Math.Min(ttl.Maximum, config.Ttl));
            useGlobal.Checked = config.UseGlobalApiKey;
            proxied.Checked = config.Proxied;
            refreshOnStart.Checked = config.RefreshOnStart;
            startWithWindows.Checked = config.StartWithWindows;
        }

        void AddRow(string labelText, Control control, ref int y)
        {
            var label = new Label { Text = labelText, Location = new Point(18, y + 4), Size = new Size(105, 24) };
            control.Location = new Point(135, y);
            control.Size = new Size(390, 24);
            Controls.Add(label);
            Controls.Add(control);
            y += 38;
        }

        void Save()
        {
            if (string.IsNullOrWhiteSpace(zone.Text) || string.IsNullOrWhiteSpace(records.Text))
            {
                MessageBox.Show("Zone and Records are required.", Text);
                return;
            }
            if (!string.IsNullOrWhiteSpace(targetIp.Text) && !DdnsClient.IsIPv4(targetIp.Text.Trim()))
            {
                MessageBox.Show("Custom IP is invalid.", Text);
                return;
            }
            Result = new AppConfig
            {
                ZoneName = zone.Text.Trim(),
                RecordNames = records.Text.Trim(),
                ApiEmail = email.Text.Trim(),
                UseGlobalApiKey = useGlobal.Checked,
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
            if (!IsIPv4(ip)) throw new InvalidOperationException("Invalid IPv4: " + ip);
            var headers = BuildHeaders(config);
            var zone = ApiGet(headers, "/zones?name=" + Uri.EscapeDataString(config.ZoneName) + "&status=active");
            var zoneList = zone["result"] as IList;
            if (zoneList == null || zoneList.Count == 0) throw new InvalidOperationException("Zone not found: " + config.ZoneName);
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
                    messages.Add("CREATED " + name + " -> " + ip);
                }
                else
                {
                    var record = (Dictionary<string, object>)records[0];
                    var current = Convert.ToString(record["content"]);
                    var proxied = Convert.ToBoolean(record["proxied"]);
                    var ttl = Convert.ToInt32(record["ttl"]);
                    if (current == ip && proxied == config.Proxied && ttl == config.Ttl)
                    {
                        messages.Add("OK " + name + " -> " + ip);
                    }
                    else
                    {
                        var body = new Dictionary<string, object> {
                            {"content", ip}, {"ttl", config.Ttl}, {"proxied", config.Proxied}
                        };
                        ApiSend(headers, "PATCH", "/zones/" + zoneId + "/dns_records/" + record["id"], body);
                        messages.Add("UPDATED " + name + " " + current + " -> " + ip);
                    }
                }
            }

            return new RefreshResult { Ip = ip, Message = string.Join("; ", messages.ToArray()) };
        }

        static Dictionary<string, string> BuildHeaders(AppConfig config)
        {
            var headers = new Dictionary<string, string>();
            if (config.UseGlobalApiKey)
            {
                if (string.IsNullOrWhiteSpace(config.ApiEmail)) throw new InvalidOperationException("Email is required.");
                var key = GetSecret(config);
                if (string.IsNullOrWhiteSpace(key)) throw new InvalidOperationException("Global API Key is required.");
                headers["X-Auth-Email"] = config.ApiEmail;
                headers["X-Auth-Key"] = key;
            }
            else
            {
                var token = GetApiToken(config);
                if (string.IsNullOrWhiteSpace(token)) throw new InvalidOperationException("API Token is required.");
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
                        errors.Add(service + " returned " + value);
                    }
                }
                catch (Exception ex)
                {
                    errors.Add(service + " failed: " + ex.Message);
                }
            }
            throw new InvalidOperationException("Could not detect public IPv4 without proxy. " + string.Join("; ", errors.ToArray()));
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
                    return string.Join("; ", messages.ToArray());
                }
            }
            catch { }
            return text;
        }
    }
}
