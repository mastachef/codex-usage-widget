using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodexUsageWidget;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        if (args.Contains("--self-test") || args.Contains("--ui-test")) { Tests.Run(args.Contains("--ui-test")).GetAwaiter().GetResult(); return; }
        using var mutex = new Mutex(true, "Local\\CodexUsageWidget", out var first);
        if (!first) { Native.PostMessage((IntPtr)0xffff, Native.ShowMessage, IntPtr.Zero, IntPtr.Zero); return; }
        Application.Run(new Widget());
    }
}
static class Native
{
    [DllImport("user32.dll")] public static extern bool ReleaseCapture();
    [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr h, int m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, int m, IntPtr w, IntPtr l);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int RegisterWindowMessage(string name);
    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)] public static extern int SetWindowTheme(IntPtr handle, string theme, string? classes);
    [DllImport("dwmapi.dll")] public static extern int DwmSetWindowAttribute(IntPtr handle, int attribute, ref int value, int size);
    public static readonly int ShowMessage = RegisterWindowMessage("CodexUsageWidget.ShowExisting");
}
sealed class Settings
{
    public List<string> Profiles { get; set; } = new();
    public bool Pinned { get; set; } = true;
    public int X { get; set; } = -1;
    public int Y { get; set; } = -1;
    public int Width { get; set; } = 410;
    public int Height { get; set; } = 510;
    public int DesignVersion { get; set; }
}
sealed class Widget : Form
{
    public static readonly string DataRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexUsageWidget");
    readonly string settingsFile = Path.Combine(DataRoot, "settings.json");
    readonly Settings settings;
    readonly FlowLayoutPanel list = new BufferedFlowPanel() { Dock = DockStyle.Fill, AutoScroll = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(6, 0, 6, 0) };
    readonly ToolTip tips = new() { AutoPopDelay = 8000, InitialDelay = 400, ReshowDelay = 150 };
    readonly NotifyIcon tray;
    readonly List<AccountCard> accounts = new();
    readonly System.Windows.Forms.Timer timer = new() { Interval = 60000 };
    bool refreshing;
    public bool SigningIn { get; set; }
    public Widget(bool preview = false)
    {
        Directory.CreateDirectory(DataRoot);
        try { settings = preview ? new Settings() : JsonSerializer.Deserialize<Settings>(File.ReadAllText(settingsFile)) ?? new(); } catch { settings = new(); }
        if (settings.DesignVersion < 1) { settings.Width = 410; settings.Height = 510; settings.DesignVersion = 1; }
        Text = "Codex Usage"; BackColor = Theme.Background; ForeColor = Theme.Text;
        using (var icon = typeof(Widget).Assembly.GetManifestResourceStream("CodexUsageWidget.Assets.CodexUsage.ico"))
            if (icon != null) { using var loaded = new Icon(icon); Icon = (Icon)loaded.Clone(); }
        Font = new Font("Segoe UI", 9); MinimumSize = new Size(360, 280);
        FormBorderStyle = FormBorderStyle.Sizable;
        ClientSize = new Size(Math.Clamp(settings.Width, 360, 1600), Math.Clamp(settings.Height, 280, 1600));
        Padding = new Padding(8);
        TopMost = settings.Pinned; DoubleBuffered = true; ResizeRedraw = true;
        StartPosition = FormStartPosition.Manual;
        var area = Screen.PrimaryScreen!.WorkingArea;
        Location = new Point(area.Right - Width - 24, area.Top + 70);
        if (Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(new Rectangle(settings.X, settings.Y, Width, Height)))) Location = new Point(settings.X, settings.Y);
        var header = new BufferedPanel { Dock = DockStyle.Top, Height = 48 };
        var brandIcon = new PictureBox { Image = (Icon ?? SystemIcons.Application).ToBitmap(), SizeMode = PictureBoxSizeMode.Zoom, Location = new Point(6, 3), Size = new Size(30, 30) };
        var title = new Label { Text = "Codex / Usage", AutoSize = true, Font = new Font("Segoe UI", 10, FontStyle.Bold), Location = new Point(45, 1) };
        var subtitle = new Label { Text = "All accounts · quota remaining", AutoSize = true, Font = new Font("Segoe UI", 7), ForeColor = Theme.Muted, Location = new Point(46, 23) };
        foreach (Control c in new Control[] { header, title, subtitle }) c.MouseDown += (_, e) => { if (e.Button == MouseButtons.Left) { Native.ReleaseCapture(); Native.SendMessage(Handle, 0xA1, (IntPtr)2, IntPtr.Zero); } };
        var pin = (SoftButton)Button("", 246, 9, 82); pin.Name = "AlwaysOnTop";
        var pinMenu = new ToolStripMenuItem("Always on top") { Checked = TopMost };
        void UpdatePin() { pin.Text = TopMost ? "●  On top" : "○  On top"; pin.Selected = TopMost; pin.ForeColor = TopMost ? Theme.Mint : Theme.Muted; pinMenu.Checked = TopMost; tips.SetToolTip(pin, "Always on top: " + (TopMost ? "On" : "Off")); pin.Invalidate(); }
        void TogglePin() { TopMost = settings.Pinned = !TopMost; UpdatePin(); if (!preview) Save(); }
        UpdatePin(); pin.Click += (_, _) => TogglePin(); pinMenu.Click += (_, _) => TogglePin();
        header.Controls.AddRange(new Control[] { brandIcon, title, subtitle, pin });
        header.SizeChanged += (_, _) => pin.Left = header.Width - pin.Width - 6;
        var footer = new BufferedPanel { Dock = DockStyle.Bottom, Height = 43 };
        var add = Button("+  Add account", 6, 8, 119); add.ForeColor = Theme.Text;
        add.Click += async (_, _) => { if (SigningIn) { MessageBox.Show(this, "Finish or cancel the current sign-in first."); return; } var card = AddProfile(Guid.NewGuid().ToString("N")); Save(); list.ScrollControlIntoView(card); await card.SignIn(); };
        var refresh = Button("↻", 276, 8, 32); refresh.Font = new Font("Segoe UI", 14); refresh.Click += async (_, _) => await RefreshAll(); tips.SetToolTip(refresh, "Refresh all accounts");
        var auto = new Label { AutoSize = true, Text = "AUTO · 60s", Font = new Font("Segoe UI", 7.5f), ForeColor = Theme.Muted, Location = new Point(150, 16) };
        tips.SetToolTip(auto, "Usage refreshes automatically once a minute");
        var update = Button("Update", 0, 8, 88); update.ForeColor = Theme.Mint; update.Visible = false;
        UpdateInfo? pendingUpdate = null;

        async Task CheckForUpdate(bool showCurrent = false)
        {
            try
            {
                pendingUpdate = await Updater.CheckAsync();
                update.Visible = pendingUpdate != null;
                if (pendingUpdate != null)
                {
                    update.Text = $"↑  v{pendingUpdate.Version.Major}.{pendingUpdate.Version.Minor}.{pendingUpdate.Version.Build}";
                    tips.SetToolTip(update, $"Install {pendingUpdate.Tag} and restart");
                }
                else if (showCurrent) MessageBox.Show(this, $"You're up to date · v{Updater.CurrentVersion.Major}.{Updater.CurrentVersion.Minor}.{Updater.CurrentVersion.Build}", "Codex Usage");
            }
            catch
            {
                if (showCurrent) MessageBox.Show(this, "Could not check GitHub for updates. Try again later.", "Codex Usage");
            }
            footer.PerformLayout();
        }

        update.Click += async (_, _) =>
        {
            if (pendingUpdate == null) { await CheckForUpdate(true); return; }
            update.Enabled = false;
            var original = update.Text;
            try
            {
                var progress = new Progress<int>(percent => update.Text = $"↓  {percent}%");
                await Updater.DownloadAndInstallAsync(pendingUpdate, progress);
                update.Text = "Restarting…";
                timer.Stop();
                Close();
            }
            catch (Exception ex)
            {
                update.Enabled = true; update.Text = original;
                MessageBox.Show(this, ex.Message, "Update failed", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        };

        footer.Controls.AddRange(new Control[] { add, update, refresh, auto });
        footer.SizeChanged += (_, _) =>
        {
            refresh.Left = footer.Width - refresh.Width - 6;
            auto.Left = refresh.Left - 86;
            update.Left = Math.Max(add.Right + 8, auto.Left - update.Width - 8);
        };
        Controls.Add(list); Controls.Add(header); Controls.Add(footer);
        list.HandleCreated += (_, _) => Native.SetWindowTheme(list.Handle, "DarkMode_Explorer", null);
        list.SizeChanged += (_, _) => FitCards();
        var menu = new ContextMenuStrip();
        menu.Items.Add("Show widget", null, (_, _) => Reveal());
        menu.Items.Add("Refresh all", null, async (_, _) => await RefreshAll());
        menu.Items.Add("Check for updates", null, async (_, _) => await CheckForUpdate(true));
        menu.Items.Add(pinMenu);
        menu.Items.Add("Start with Windows", null, (_, _) => ToggleStartup());
        menu.Items.Add("Quit", null, (_, _) => Close());
        tray = new NotifyIcon { Icon = Icon, Text = "Codex Usage", Visible = true, ContextMenuStrip = menu };
        tray.DoubleClick += (_, _) => Reveal();
        foreach (var id in settings.Profiles.ToArray().Where(id => Guid.TryParseExact(id, "N", out _)).Distinct()) AddProfile(id);
        if (accounts.Count == 0) AddProfile(Guid.NewGuid().ToString("N"));
        if (!preview) Save();
        if (!preview) Shown += async (_, _) => { await RefreshAll(); await CheckForUpdate(); };
        timer.Tick += async (_, _) => await RefreshAll(); if (!preview) timer.Start();
        FormClosing += (_, _) => { timer.Stop(); settings.X = Left; settings.Y = Top; settings.Width = ClientSize.Width; settings.Height = ClientSize.Height; if (!preview) Save(); tray.Dispose(); foreach (var card in accounts) card.Shutdown(); };
    }
    public static Button Button(string text, int x, int y, int width) => new SoftButton { Text = text, Location = new Point(x, y), Size = new Size(width, 27), TabStop = true };
    AccountCard AddProfile(string id)
    {
        if (!settings.Profiles.Contains(id)) settings.Profiles.Add(id);
        var card = new AccountCard(this, id); accounts.Add(card); list.Controls.Add(card); FitCards(); return card;
    }
    void FitCards() { foreach (var card in list.Controls.OfType<AccountCard>()) card.Width = Math.Max(306, list.ClientSize.Width - list.Padding.Horizontal); }
    public async Task Remove(AccountCard card)
    {
        if (MessageBox.Show(this, "Sign out and remove this account from the widget?", "Remove account", MessageBoxButtons.YesNo) != DialogResult.Yes) return;
        try { await card.Logout(); } catch { MessageBox.Show(this, "Could not sign out. Refresh the account and retry."); return; }
        card.Shutdown(); accounts.Remove(card); settings.Profiles.Remove(card.ProfileId); list.Controls.Remove(card); card.Dispose(); Save();
    }
    void Save() { try { File.WriteAllText(settingsFile + ".tmp", JsonSerializer.Serialize(settings)); File.Move(settingsFile + ".tmp", settingsFile, true); } catch { } }
    async Task RefreshAll()
    {
        if (refreshing) return;
        refreshing = true;
        try { foreach (var card in accounts.ToArray()) if (!card.IsDisposed) await card.RefreshUsage(); }
        finally { refreshing = false; }
    }
    void Reveal() { Show(); WindowState = FormWindowState.Normal; Activate(); }
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == Native.ShowMessage) Reveal();
        base.WndProc(ref m);
    }
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        // Native Windows 11 polish. Unsupported attributes are safely ignored on older Windows.
        int enabled = 1;
        Native.DwmSetWindowAttribute(Handle, 20, ref enabled, sizeof(int)); // immersive dark mode
        int corner = 2;
        Native.DwmSetWindowAttribute(Handle, 33, ref corner, sizeof(int)); // rounded outer corners
        int backdrop = 2;
        Native.DwmSetWindowAttribute(Handle, 38, ref backdrop, sizeof(int)); // Mica system backdrop
    }
    protected override void OnResizeEnd(EventArgs e)
    {
        base.OnResizeEnd(e);
        FitCards(); Invalidate(true); Update();
    }
    void ToggleStartup()
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        if (key.GetValue("CodexUsageWidget") != null) { key.DeleteValue("CodexUsageWidget"); MessageBox.Show(this, "Start with Windows disabled."); }
        else { key.SetValue("CodexUsageWidget", "\"" + Application.ExecutablePath + "\""); MessageBox.Show(this, "Start with Windows enabled."); }
    }
}

sealed class AccountCard : Panel
{
    public static readonly Color Teal = Theme.Mint;
    readonly Widget owner;
    public string ProfileId { get; }
    Rpc? rpc;
    bool busy, loggingIn, stopped, expanded, hovered;
    string? loginId, signInUrl;
    string email = "Connect an account", plan = "", status = "Sign in with your ChatGPT account";
    JsonNode? data, tokenUsage;
    DateTime? updated;
    readonly Button login, copyLink, analytics, remove;
    readonly System.Windows.Forms.Timer clock = new() { Interval = 15000 };
    public AccountCard(Widget parent, string id)
    {
        owner = parent; ProfileId = id; Size = new Size(360, 148); Margin = new Padding(0, 0, 0, 8); DoubleBuffered = true; ResizeRedraw = true; BackColor = Theme.Background;
        login = Widget.Button("Sign in", 14, 106, 90); login.Click += async (_, _) => await SignIn();
        copyLink = Widget.Button("Copy sign-in link", 110, 106, 166);
        copyLink.Click += async (_, _) =>
        {
            if (busy || stopped) return;
            if (!loggingIn) await SignIn(openBrowser: false);
            if (signInUrl == null) return;
            try { Clipboard.SetText(signInUrl); status = "Link copied · paste into your preferred browser"; }
            catch (ExternalException) { status = "Clipboard busy · click Copy sign-in link again"; }
            Invalidate();
        };
        analytics = Widget.Button("Analytics", 14, 106, 90); analytics.ForeColor = Theme.Mint; analytics.Visible = false;
        analytics.Click += (_, _) => { using var form = new AnalyticsForm(ProfileId, email, () => tokenUsage); form.ShowDialog(owner); };
        remove = Widget.Button("Remove", 282, 106, 64); remove.ForeColor = Theme.Muted; remove.Click += async (_, _) => await owner.Remove(this);
        Controls.AddRange(new Control[] { login, copyLink, analytics, remove }); clock.Tick += (_, _) => Invalidate(); clock.Start();
        SizeChanged += (_, _) => { remove.Left = Width - 78; copyLink.Width = Width - 194; Invalidate(); };
        MouseEnter += (_, _) => { hovered = true; Invalidate(); };
        MouseLeave += (_, _) => { hovered = false; Invalidate(); };
        Cursor = Cursors.Hand;
        Click += (_, _) => { if (data != null) { expanded = !expanded; LayoutCard(); Invalidate(); } };
    }
    async Task<Rpc> Connect()
    {
        if (rpc != null) return rpc;
        var connected = await Rpc.Start(Path.Combine(Widget.DataRoot, "profiles", ProfileId));
        if (stopped) { connected.Dispose(); throw new ObjectDisposedException(nameof(AccountCard)); }
        rpc = connected;
        rpc.Notification += (method, p) =>
        {
            if (method == "account/login/completed" && !stopped && IsHandleCreated)
                BeginInvoke(new Action(async () => { loggingIn = false; loginId = null; signInUrl = null; owner.SigningIn = false; login.Text = "Sign in"; if (p?["success"]?.GetValue<bool>() == true) await RefreshUsage(); else { status = "Sign-in cancelled or failed. Try again."; Invalidate(); } }));
        };
        return rpc;
    }
    public async Task SignIn(bool openBrowser = true)
    {
        if (busy || stopped) return;
        if (loggingIn)
        {
            try { await (await Connect()).Call("account/login/cancel", new { loginId }); } catch { rpc?.Dispose(); rpc = null; }
            loggingIn = false; owner.SigningIn = false; loginId = null; signInUrl = null; login.Text = "Sign in"; status = "Sign-in cancelled"; Invalidate(); return;
        }
        if (owner.SigningIn) { MessageBox.Show(owner, "Finish or cancel the current sign-in first."); return; }
        owner.SigningIn = true; busy = true; signInUrl = null; status = "Preparing secure sign-in…"; Invalidate();
        try
        {
            var result = await (await Connect()).Call("account/login/start", new { type = "chatgpt" });
            loginId = result["loginId"]?.ToString();
            var url = new Uri(result["authUrl"]!.ToString());
            if (url.Scheme != "https" || !(url.Host == "auth.openai.com" || url.Host == "chatgpt.com" || url.Host.EndsWith(".openai.com"))) throw new Exception("Unexpected sign-in URL.");
            signInUrl = url.AbsoluteUri;
            loggingIn = true; login.Text = "Cancel"; status = "Choose the intended account in your browser";
            if (openBrowser)
            {
                try { Process.Start(new ProcessStartInfo(signInUrl) { UseShellExecute = true }); }
                catch { status = "Browser unavailable · use Copy sign-in link"; }
            }
        }
        catch { status = "Sign-in failed. Refresh and try again."; owner.SigningIn = false; loggingIn = false; signInUrl = null; rpc?.Dispose(); rpc = null; }
        finally { busy = false; Invalidate(); }
    }
    public async Task RefreshUsage()
    {
        if (busy || loggingIn || stopped) return; busy = true;
        try
        {
            var server = await Connect();
            var account = (await server.Call("account/read", new { refreshToken = false }))["account"];
            if (account == null) { email = "Connect an account"; plan = ""; data = null; tokenUsage = null; updated = null; status = "Sign in with your ChatGPT account"; login.Text = "Sign in"; return; }
            email = account["email"]?.ToString() ?? "ChatGPT account"; plan = account["planType"]?.ToString() ?? ""; login.Text = "Reconnect";
            data = await server.Call("account/rateLimits/read");
            try { tokenUsage = await server.Call("account/usage/read"); } catch { tokenUsage = null; }
            UsageHistoryStore.Append(ProfileId, data, tokenUsage);
            updated = DateTime.Now; status = "";
        }
        catch { status = "Unavailable · Refresh or reconnect"; rpc?.Dispose(); rpc = null; }
        finally { busy = false; if (!stopped) { LayoutCard(); Invalidate(); } }
    }
    public async Task Logout() { if (loggingIn && loginId != null) await (await Connect()).Call("account/login/cancel", new { loginId }); await (await Connect()).Call("account/logout"); }
    public void Shutdown() { stopped = true; clock.Dispose(); rpc?.Dispose(); if (loggingIn) owner.SigningIn = false; }
    void LayoutCard()
    {
        int rows = data == null ? 0 : Usage.Buckets(data).Count();
        Height = data == null ? 148 : expanded ? 120 + rows * 148 + (data["rateLimitResetCredits"] != null ? 22 : 0) : 120;
        if (data == null)
        {
            analytics.Visible = false;
            login.Visible = copyLink.Visible = remove.Visible = true;
            login.Left = 14;
        }
        else
        {
            analytics.Visible = expanded;
            login.Visible = remove.Visible = expanded;
            copyLink.Visible = false;
            analytics.Left = 14;
            login.Left = 110;
        }
        analytics.Top = login.Top = copyLink.Top = remove.Top = Height - 41;
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e); var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
        using var shape = Theme.Round(new RectangleF(.5f, .5f, Width - 1, Height - 1), 13);
        using var surface = new LinearGradientBrush(ClientRectangle, hovered ? Color.FromArgb(24, 45, 62) : Color.FromArgb(19, 36, 51), Color.FromArgb(12, 24, 34), 65f);
        g.FillPath(surface, shape);
        using var edge = new Pen(hovered ? Color.FromArgb(62, 111, 139) : Color.FromArgb(42, 64, 82)); g.DrawPath(edge, shape);
        using var topLight = new Pen(Color.FromArgb(24, 180, 216, 255)); g.DrawLine(topLight, 16, 1.5f, Width - 16, 1.5f);
        var badgeColor = Theme.AvatarColor(ProfileId);
        using var avatar = new LinearGradientBrush(new Rectangle(14, 13, 29, 29), badgeColor, Color.FromArgb(badgeColor.R * 3 / 4, badgeColor.G * 3 / 4, badgeColor.B * 3 / 4), 90f); g.FillEllipse(avatar, 14, 13, 29, 29);
        using var badgeEdge = new Pen(Color.FromArgb(65, 220, 233, 255)); g.DrawEllipse(badgeEdge, 14, 13, 29, 29);
        Theme.Label(g, data == null && plan.Length == 0 ? "+" : email[..1].ToUpperInvariant(), new Rectangle(22, 15, 20, 24), 11, Theme.Text, true);
        Theme.Label(g, email, new Rectangle(52, 10, Width - 86, 22), 9.5f, Theme.Text, true);
        var info = plan.Length > 0 ? plan.ToUpperInvariant() : "CHATGPT ACCOUNT";
        if (data != null && status.Length > 0) info += "  ·  " + status;
        else if (updated is { } time) info += $"  ·  {time:h:mm tt}";
        Theme.Label(g, info, new Rectangle(52, 30, Width - 84, 16), 7.25f, status.StartsWith("Unavailable") ? Color.Salmon : Theme.Muted);
        if (data != null)
        {
            using var chevron = new Pen(Theme.Muted, 1.3f);
            float x = Width - 24, y = 25;
            g.DrawLines(chevron, expanded ? new[] { new PointF(x - 3, y + 2), new PointF(x, y - 1), new PointF(x + 3, y + 2) } : new[] { new PointF(x - 3, y - 1), new PointF(x, y + 2), new PointF(x + 3, y - 1) });
        }
        if (data != null && !expanded)
        {
            var bucket = data["rateLimitsByLimitId"]?["codex"] ?? Usage.Buckets(data).FirstOrDefault().Item2;
            int column = (Width - 44) / 2;
            using var divider = new Pen(Color.FromArgb(35, 55, 72)); g.DrawLine(divider, Width / 2f, 59, Width / 2f, 108);
            CompactWindow(g, bucket?["primary"], "5-hour", 14, column, Theme.Mint);
            CompactWindow(g, bucket?["secondary"], "Weekly", 30 + column, column, Theme.Lilac);
            return;
        }
        var updateText = status.Length > 0 ? status : updated is { } t ? $"Updated {t:h:mm:ss tt} · local reset times" : "No usage data yet";
        Theme.Label(g, updateText, new Rectangle(14, 55, Width - 28, 18), 7.5f, status.StartsWith("Unavailable") ? Color.Salmon : Theme.Muted);
        int detailY = 80;
        if (data != null)
        {
            foreach (var (name, bucket) in Usage.Buckets(data))
            {
                Theme.Label(g, name.ToUpperInvariant(), new Rectangle(14, detailY, Width - 28, 16), 7.5f, Theme.Muted, true); detailY += 18;
                Window(g, bucket["primary"], "5-hour", detailY, Theme.Mint); detailY += 54;
                Window(g, bucket["secondary"], "Weekly", detailY, Theme.Lilac); detailY += 54;
                var credits = bucket["credits"];
                if (credits != null) Theme.Label(g, credits["unlimited"]?.GetValue<bool>() == true ? "Credits  ·  Unlimited" : "Credits  ·  " + (credits["balance"]?.ToString() ?? "Unavailable"), new Rectangle(14, detailY, Width - 28, 18), 8, Theme.Muted);
                detailY += 22;
            }
            if (data["rateLimitResetCredits"] is { } reset) Theme.Label(g, $"Reset credits  ·  {reset["availableCount"]}", new Rectangle(14, detailY, Width - 28, 18), 8, Theme.Muted);
        }
        else Theme.Label(g, loggingIn ? "Finish signing in to see your limits." : "Your limits, together in one place.", new Rectangle(14, 76, Width - 28, 18), 8, Theme.Muted);
    }
    static string WindowName(JsonNode? w, string fallback)
    {
        var mins = w?["windowDurationMins"]?.GetValue<int?>();
        return mins == 10080 ? "Weekly" : mins == 300 ? "5-hour" : mins is > 0 ? mins % 60 == 0 ? $"{mins / 60}h window" : $"{mins}m window" : fallback;
    }
    void CompactWindow(Graphics g, JsonNode? w, string name, int x, int width, Color accent)
    {
        var value = Usage.Remaining(w); var color = value < 15 ? Color.Salmon : accent;
        Theme.Label(g, WindowName(w, name), new Rectangle(x, 55, width / 2, 20), 8, Theme.Muted);
        Theme.Label(g, value is { } n ? $"{n:0.#}%" : "—", new Rectangle(x + width / 2, 51, width / 2, 25), 14, value < 15 ? Color.Salmon : Theme.Text, true, true);
        Theme.Bar(g, new RectangleF(x, 80, width, 6), value, color);
        var reset = width >= 245 ? Usage.Reset(w) : Usage.Reset(w).Split(" · ")[0];
        Theme.Label(g, reset, new Rectangle(x, 92, width, 16), 7.25f, Theme.Muted);
    }
    void Window(Graphics g, JsonNode? w, string name, int y, Color accent)
    {
        var value = Usage.Remaining(w); var color = value < 15 ? Color.Salmon : accent;
        Theme.Label(g, WindowName(w, name), new Rectangle(14, y, 130, 20), 8.5f, Theme.Text);
        Theme.Label(g, value is { } n ? $"{n:0.#}% left" : "Unavailable", new Rectangle(Width - 134, y, 120, 20), 9, color, true, true);
        Theme.Bar(g, new RectangleF(14, y + 23, Width - 28, 6), value, color);
        Theme.Label(g, Usage.Reset(w), new Rectangle(14, y + 32, Width - 28, 16), 7.5f, Theme.Muted);
    }

}

static class Tests
{
    public static async Task Run(bool uiOnly = false)
    {
        var results = new List<string>();
        void Check(bool pass, string label) { if (!pass) throw new Exception(label); results.Add("PASS " + label); }
        using (var widget = new Widget(true))
        {
            var panel = widget.Controls.OfType<FlowLayoutPanel>().Single();
            panel.Controls.Clear();
            var previews = new List<AccountCard>();
            for (var i = 0; i < 3; i++)
            {
                var card = new AccountCard(widget, "preview-" + i);
                void Set(string name, object value) => typeof(AccountCard).GetField(name, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.SetValue(card, value);
                Set("email", $"account{i + 1}@example.com"); Set("plan", "plus"); Set("status", ""); Set("updated", DateTime.Now);
                Set("data", JsonSerializer.SerializeToNode(new { rateLimits = new { limitId = "codex", primary = new { usedPercent = 24 + i * 29, windowDurationMins = 300, resetsAt = DateTimeOffset.Now.AddHours(3).ToUnixTimeSeconds() }, secondary = new { usedPercent = 15 + i * 23, windowDurationMins = 10080, resetsAt = DateTimeOffset.Now.AddDays(4).ToUnixTimeSeconds() }, credits = new { balance = "0", unlimited = false } }, rateLimitResetCredits = new { availableCount = 1 } })!);
                typeof(AccountCard).GetMethod("LayoutCard", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(card, null);
                previews.Add(card); panel.Controls.Add(card);
            }
            widget.Location = new Point(-10000, -10000); widget.Show(); Application.DoEvents(); typeof(Widget).GetMethod("FitCards", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(widget, null); panel.PerformLayout();
            using var bitmap = new Bitmap(widget.Width, widget.Height); widget.DrawToBitmap(bitmap, new Rectangle(Point.Empty, widget.Size)); bitmap.Save(Path.Combine(AppContext.BaseDirectory, "preview-compact.png"));
            typeof(AccountCard).GetField("expanded", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.SetValue(previews[0], true);
            typeof(AccountCard).GetMethod("LayoutCard", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(previews[0], null);
            panel.PerformLayout(); widget.DrawToBitmap(bitmap, new Rectangle(Point.Empty, widget.Size)); bitmap.Save(Path.Combine(AppContext.BaseDirectory, "preview-expanded.png"));
            Check(previews[0].Height > previews[1].Height, "Click expansion reveals additional detail space");
            widget.ClientSize = new Size(680, 820); Application.DoEvents();
            Check(previews.All(card => card.Width > 550), "Account cards expand with the window");
            var corner = new Point(widget.Right - 2, widget.Bottom - 2);
            var hit = Native.SendMessage(widget.Handle, 0x84, IntPtr.Zero, (IntPtr)((corner.Y << 16) | (corner.X & 0xffff)));
            Check(hit == (IntPtr)17, "Bottom-right corner exposes native resize handle");
            Check(widget.FormBorderStyle == FormBorderStyle.Sizable && widget.Region == null, "Windows owns frame, caption buttons and resize region");
            for (int step = 0; step < 24; step++)
            {
                widget.ClientSize = new Size(360 + step * 13, 300 + step * 17);
                Application.DoEvents();
                Check(widget.Visible && widget.Region == null && panel.Visible, $"Resize step {step + 1} keeps content visible");
            }
            widget.ClientSize = new Size(680, 820); Application.DoEvents();
            var pin = widget.Controls.OfType<Panel>().SelectMany(panel => panel.Controls.OfType<Button>()).Single(button => button.Name == "AlwaysOnTop");
            pin.PerformClick(); Check(!widget.TopMost && !((SoftButton)pin).Selected, "Always-on-top control turns off");
            pin.PerformClick(); Check(widget.TopMost && ((SoftButton)pin).Selected, "Always-on-top control turns on");
            using (var resized = new Bitmap(widget.Width, widget.Height)) { widget.DrawToBitmap(resized, new Rectangle(Point.Empty, widget.Size)); resized.Save(Path.Combine(AppContext.BaseDirectory, "preview-resized.png")); }
            foreach (var card in previews) card.Shutdown();
            widget.Close();
        }
        SynchronizationContext.SetSynchronizationContext(null);
        if (uiOnly) { File.WriteAllLines(Path.Combine(AppContext.BaseDirectory, "ui-test-results.txt"), results); return; }
        Check(Usage.Remaining(null) == null, "Unknown usage is not zero");
        Check(Usage.Remaining(JsonNode.Parse("{\"usedPercent\":27}")) == 73, "Used percentage converts to remaining");
        Check(Usage.Remaining(JsonNode.Parse("{\"usedPercent\":120}")) == 0, "Remaining clamps to zero");
        Check(Usage.Reset(JsonNode.Parse("{\"resetsAt\":1}")).StartsWith("Reset due"), "Elapsed reset never claims restored quota");
        Check(Usage.Buckets(JsonNode.Parse("{\"rateLimits\":{},\"rateLimitsByLimitId\":{\"a\":{},\"b\":{}}}")!).Count() == 2, "All usage buckets retained");
        var homes = new[] { Path.Combine(Widget.DataRoot, "test-" + Guid.NewGuid().ToString("N")), Path.Combine(Widget.DataRoot, "test-" + Guid.NewGuid().ToString("N")) };
        foreach (var home in homes)
        {
            using var rpc = await Rpc.Start(home);
            Check((await rpc.Call("account/read", new { refreshToken = false }))["account"] == null, "Isolated profile starts logged out");
            Check(!File.Exists(Path.Combine(home, "auth.json")), "No plaintext credentials created");
            var login = await rpc.Call("account/login/start", new { type = "chatgpt" });
            Check(login["authUrl"]?.ToString().StartsWith("https://") == true, "Real browser OAuth flow starts");
            await rpc.Call("account/login/cancel", new { loginId = login["loginId"]!.ToString() });
            Check((await rpc.Call("account/read", new { refreshToken = false }))["account"] == null, "Cancelled sign-in leaves isolated profile logged out");
        }
        File.WriteAllLines(Path.Combine(AppContext.BaseDirectory, "test-results.txt"), results);
    }
}