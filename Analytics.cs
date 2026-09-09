using System.Drawing.Drawing2D;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodexUsageWidget;

sealed record UsageHistorySample(
    DateTime TimestampUtc,
    double? FiveHourRemaining,
    double? WeeklyRemaining,
    long? LifetimeTokens);

static class UsageHistoryStore
{
    static string DirectoryPath => Path.Combine(Widget.DataRoot, "history");

    static string PathFor(string profileId)
    {
        Directory.CreateDirectory(DirectoryPath);
        return Path.Combine(DirectoryPath, profileId + ".jsonl");
    }

    public static void Append(string profileId, JsonNode rateLimits, JsonNode? tokenUsage)
    {
        try
        {
            var sample = new UsageHistorySample(
                DateTime.UtcNow,
                RemainingForDuration(rateLimits, 300),
                RemainingForDuration(rateLimits, 10080),
                LongValue(tokenUsage?["summary"]?["lifetimeTokens"]));

            var path = PathFor(profileId);
            File.AppendAllText(path, JsonSerializer.Serialize(sample) + Environment.NewLine);

            var info = new FileInfo(path);
            if (info.Length > 8 * 1024 * 1024)
            {
                var keep = File.ReadLines(path).TakeLast(20000).ToArray();
                File.WriteAllLines(path, keep);
            }
        }
        catch { }
    }

    public static List<UsageHistorySample> Load(string profileId, TimeSpan? age = null)
    {
        try
        {
            var path = PathFor(profileId);
            if (!File.Exists(path)) return new();
            var cutoff = DateTime.UtcNow - (age ?? TimeSpan.FromDays(14));
            var result = new List<UsageHistorySample>();
            foreach (var line in File.ReadLines(path))
            {
                try
                {
                    var item = JsonSerializer.Deserialize<UsageHistorySample>(line);
                    if (item != null && item.TimestampUtc >= cutoff) result.Add(item);
                }
                catch { }
            }
            return result;
        }
        catch { return new(); }
    }

    static double? RemainingForDuration(JsonNode data, int minutes)
    {
        foreach (var (_, bucket) in Usage.Buckets(data))
        {
            foreach (var slot in new[] { "primary", "secondary" })
            {
                var window = bucket[slot];
                if (window?["windowDurationMins"]?.GetValue<int?>() == minutes)
                    return Usage.Remaining(window);
            }
        }
        return null;
    }

    static long? LongValue(JsonNode? value)
    {
        if (value is not JsonValue v) return null;
        if (v.TryGetValue<long>(out var n)) return n;
        if (v.TryGetValue<double>(out var d) && double.IsFinite(d)) return (long)d;
        return null;
    }
}

sealed class AnalyticsForm : Form
{
    readonly string profileId;
    readonly AnalyticsSurface surface;
    readonly System.Windows.Forms.Timer timer = new() { Interval = 5000 };
    readonly Label scanStatus = new();
    readonly Label efficiency = new();
    readonly ComboBox range = new();
    readonly ComboBox modelFilter = new();
    readonly ComboBox effortFilter = new();
    readonly DataGridView requests = Grid();
    readonly DataGridView models = Grid();
    readonly DataGridView sessions = Grid();
    readonly DataGridView review = Grid();
    DeepTelemetrySnapshot? local;

    public AnalyticsForm(string profileId, string accountName, Func<JsonNode?> tokenUsageProvider)
    {
        this.profileId = profileId;
        Text = "Codex Usage Analytics";
        BackColor = Theme.Background;
        ForeColor = Theme.Text;
        Font = new Font("Segoe UI", 9);
        MinimumSize = new Size(900, 600);
        ClientSize = new Size(1220, 760);
        StartPosition = FormStartPosition.CenterParent;

        var top = new Panel { Dock = DockStyle.Top, Height = 76, BackColor = Theme.Background };
        var title = new Label { Text = "Usage analytics", AutoSize = true, Font = new Font("Segoe UI", 15, FontStyle.Bold), ForeColor = Theme.Text, Location = new Point(14, 8) };
        var account = new Label { Text = accountName, AutoSize = true, Font = new Font("Segoe UI", 8.5f), ForeColor = Theme.Muted, Location = new Point(15, 35) };
        efficiency.AutoSize = true; efficiency.ForeColor = Theme.Muted; efficiency.Location = new Point(250, 12); efficiency.Font = new Font("Segoe UI", 8.25f);
        scanStatus.AutoSize = false; scanStatus.Width = 640; scanStatus.Height = 20; scanStatus.ForeColor = Theme.Muted; scanStatus.Location = new Point(250, 36);

        range.DropDownStyle = ComboBoxStyle.DropDownList; range.Items.AddRange(new object[] { "24 hours", "7 days", "30 days" }); range.SelectedIndex = 1;
        range.Location = new Point(15, 51); range.Width = 92; range.SelectedIndexChanged += (_, _) => PopulateLocalViews();

        modelFilter.DropDownStyle = ComboBoxStyle.DropDownList; modelFilter.Items.Add("All models"); modelFilter.SelectedIndex = 0;
        modelFilter.Location = new Point(112, 51); modelFilter.Width = 160; modelFilter.SelectedIndexChanged += (_, _) => PopulateLocalViews();

        effortFilter.DropDownStyle = ComboBoxStyle.DropDownList; effortFilter.Items.Add("All efforts"); effortFilter.SelectedIndex = 0;
        effortFilter.Location = new Point(277, 51); effortFilter.Width = 115; effortFilter.SelectedIndexChanged += (_, _) => PopulateLocalViews();

        var rescan = Widget.Button("Rescan", 0, 47, 78); rescan.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        var export = Widget.Button("Export CSV", 0, 47, 92); export.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        top.Controls.AddRange(new Control[] { title, account, efficiency, scanStatus, range, modelFilter, effortFilter, rescan, export });
        top.SizeChanged += (_, _) => { export.Left = top.Width - export.Width - 14; rescan.Left = export.Left - rescan.Width - 7; };

        var tabs = new TabControl { Dock = DockStyle.Fill, Appearance = TabAppearance.Normal };
        var overview = Page("Overview");
        surface = new AnalyticsSurface(profileId, accountName, tokenUsageProvider) { Dock = DockStyle.Fill };
        overview.Controls.Add(surface);

        var requestPage = Page("Model requests");
        AddRequestColumns(requests); requestPage.Controls.Add(requests);
        var modelPage = Page("Models / effort");
        AddModelColumns(models); modelPage.Controls.Add(models);
        var sessionPage = Page("Sessions");
        AddSessionColumns(sessions); sessionPage.Controls.Add(sessions);
        var reviewPage = Page("Optimization flags");
        AddReviewColumns(review); reviewPage.Controls.Add(review);
        tabs.TabPages.AddRange(new[] { overview, requestPage, modelPage, sessionPage, reviewPage });

        Controls.Add(tabs);
        Controls.Add(top);

        rescan.Click += async (_, _) => await LoadLocal(true);
        export.Click += (_, _) => ExportCsv();

        timer.Tick += (_, _) => surface.RefreshData();
        Shown += async (_, _) =>
        {
            surface.RefreshData();
            timer.Start();
            await LoadLocal(false);
        };
        FormClosed += (_, _) => timer.Dispose();
    }

    async Task LoadLocal(bool force)
    {
        scanStatus.Text = "Scanning local Codex usage metadata…";
        try
        {
            local = await DeepTelemetry.ScanAsync(profileId, force);
            RebuildFilters();
            PopulateLocalViews();
            var roots = local.ScannedRoots.Count == 0 ? "no session roots" : string.Join(" · ", local.ScannedRoots.Select(ShortPath).Take(2));
            scanStatus.Text = local.Warning ?? $"{local.Requests.Count:N0} model requests · {local.Sessions.Count:N0} sessions · {local.FilesScanned:N0} rollout files · {roots}";
        }
        catch (Exception ex)
        {
            scanStatus.Text = "Local telemetry unavailable · " + ex.Message;
        }
    }

    void RebuildFilters()
    {
        if (local == null) return;
        var selectedModel = modelFilter.SelectedItem?.ToString() ?? "All models";
        var selectedEffort = effortFilter.SelectedItem?.ToString() ?? "All efforts";

        modelFilter.BeginUpdate();
        modelFilter.Items.Clear();
        modelFilter.Items.Add("All models");
        foreach (var value in local.Requests.Select(r => r.Model).Where(v => !string.IsNullOrWhiteSpace(v)).Distinct().OrderBy(v => v)) modelFilter.Items.Add(value);
        modelFilter.SelectedItem = modelFilter.Items.Contains(selectedModel) ? selectedModel : "All models";
        modelFilter.EndUpdate();

        effortFilter.BeginUpdate();
        effortFilter.Items.Clear();
        effortFilter.Items.Add("All efforts");
        foreach (var value in local.Requests.Select(r => r.Effort).Where(v => !string.IsNullOrWhiteSpace(v)).Distinct().OrderBy(v => v)) effortFilter.Items.Add(value);
        effortFilter.SelectedItem = effortFilter.Items.Contains(selectedEffort) ? selectedEffort : "All efforts";
        effortFilter.EndUpdate();
    }

    List<LocalRequestUsage> Filtered()
    {
        if (local == null) return new();
        var age = range.SelectedIndex switch { 0 => TimeSpan.FromHours(24), 2 => TimeSpan.FromDays(30), _ => TimeSpan.FromDays(7) };
        var cutoff = DateTime.UtcNow - age;
        var model = modelFilter.SelectedItem?.ToString();
        var effort = effortFilter.SelectedItem?.ToString();
        return local.Requests.Where(r => r.TimestampUtc >= cutoff)
            .Where(r => model == "All models" || string.IsNullOrWhiteSpace(model) || r.Model == model)
            .Where(r => effort == "All efforts" || string.IsNullOrWhiteSpace(effort) || r.Effort == effort)
            .OrderByDescending(r => r.TimestampUtc).ToList();
    }

    void PopulateLocalViews()
    {
        if (local == null) return;
        var rows = Filtered();
        requests.Rows.Clear();
        foreach (var r in rows.Take(5000))
        {
            requests.Rows.Add(
                r.TimestampUtc.ToLocalTime().ToString("M/d h:mm:ss tt"),
                r.Project,
                r.Model,
                r.Effort,
                DeepTelemetry.Format(r.TotalTokens),
                DeepTelemetry.Format(r.InputTokens),
                DeepTelemetry.Format(r.CachedInputTokens),
                $"{r.CacheHitPercent:0.#}%",
                DeepTelemetry.Format(r.UncachedInputTokens),
                DeepTelemetry.Format(r.CacheWriteInputTokens),
                DeepTelemetry.Format(r.OutputTokens),
                DeepTelemetry.Format(r.ReasoningOutputTokens),
                r.ContextPercent is { } cp ? $"{cp:0.#}%" : "—",
                r.FiveHourDelta is { } f ? $"+{f:0.###}%" : "—",
                r.WeeklyDelta is { } w ? $"+{w:0.###}%" : "—",
                r.CompactionsInTurn,
                ShortId(r.ThreadId),
                ShortId(r.TurnId));
        }

        var modelRows = rows.GroupBy(r => (r.Model, r.Effort)).Select(g =>
        {
            long input = g.Sum(r => r.InputTokens), cached = g.Sum(r => r.CachedInputTokens), total = g.Sum(r => r.TotalTokens);
            var context = g.Where(r => r.ContextPercent != null).Select(r => r.ContextPercent!.Value).ToList();
            var measured5 = g.Where(r => r.FiveHourDelta is > 0).ToList();
            var measuredW = g.Where(r => r.WeeklyDelta is > 0).ToList();
            double? q5 = measured5.Sum(r => r.TotalTokens) > 0 ? measured5.Sum(r => r.FiveHourDelta ?? 0) / (measured5.Sum(r => r.TotalTokens) / 1_000_000d) : null;
            double? qw = measuredW.Sum(r => r.TotalTokens) > 0 ? measuredW.Sum(r => r.WeeklyDelta ?? 0) / (measuredW.Sum(r => r.TotalTokens) / 1_000_000d) : null;
            return new { g.Key.Model, g.Key.Effort, Count = g.Count(), Total = total, Avg = g.Average(r => (double)r.TotalTokens), Input = input, Cached = cached, Uncached = Math.Max(0, input - cached), Output = g.Sum(r => r.OutputTokens), Reasoning = g.Sum(r => r.ReasoningOutputTokens), Cache = input <= 0 ? 0 : 100d * cached / input, Context = context.Count > 0 ? context.Average() : (double?)null, Compactions = g.Sum(r => r.CompactionsInTurn), Q5 = q5, QW = qw };
        }).OrderByDescending(x => x.Total).ToList();

        models.Rows.Clear();
        foreach (var m in modelRows)
            models.Rows.Add(m.Model, m.Effort, m.Count, DeepTelemetry.Format(m.Total), DeepTelemetry.Format((long)m.Avg), DeepTelemetry.Format(m.Input), DeepTelemetry.Format(m.Cached), $"{m.Cache:0.#}%", DeepTelemetry.Format(m.Uncached), DeepTelemetry.Format(m.Output), DeepTelemetry.Format(m.Reasoning), m.Context is { } c ? $"{c:0.#}%" : "—", m.Compactions, m.Q5 is { } q5 ? $"{q5:0.###}%" : "—", m.QW is { } qw ? $"{qw:0.###}%" : "—");

        var sessionRows = rows.GroupBy(r => r.ThreadId).Select(g =>
        {
            long input = g.Sum(r => r.InputTokens), cached = g.Sum(r => r.CachedInputTokens);
            var contexts = g.Where(r => r.ContextPercent != null).Select(r => r.ContextPercent!.Value).ToList();
            return new { Thread = g.Key, Project = g.Select(r => r.Project).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? "", Source = g.Select(r => r.Source).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? "", Models = string.Join(", ", g.Select(r => r.Model).Distinct().Take(3)), First = g.Min(r => r.TimestampUtc), Last = g.Max(r => r.TimestampUtc), Count = g.Count(), Total = g.Sum(r => r.TotalTokens), Cache = input <= 0 ? 0 : 100d * cached / input, Peak = contexts.Count > 0 ? contexts.Max() : (double?)null, Compactions = g.Sum(r => r.CompactionsInTurn) };
        }).OrderByDescending(x => x.Total).ToList();

        sessions.Rows.Clear();
        foreach (var row in sessionRows)
            sessions.Rows.Add(row.Project, row.Source, row.Models, row.First.ToLocalTime().ToString("M/d h:mm tt"), row.Last.ToLocalTime().ToString("M/d h:mm tt"), row.Count, DeepTelemetry.Format(row.Total), $"{row.Cache:0.#}%", row.Peak is { } p ? $"{p:0.#}%" : "—", row.Compactions, ShortId(row.Thread));

        var allowedTurns = rows.Select(r => r.TurnId).ToHashSet(StringComparer.Ordinal);
        var projects = rows.GroupBy(r => r.ThreadId).ToDictionary(g => g.Key, g => g.Select(r => r.Project).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? "", StringComparer.Ordinal);
        review.Rows.Clear();
        foreach (var flag in local.Flags.Where(f => allowedTurns.Contains(f.TurnId)).Take(3000))
            review.Rows.Add(flag.Severity, flag.Signal, flag.Detail, projects.GetValueOrDefault(flag.ThreadId), flag.TimestampUtc.ToLocalTime().ToString("M/d h:mm:ss tt"), DeepTelemetry.Format(flag.Tokens), ShortId(flag.ThreadId), ShortId(flag.TurnId));

        long totalTokens = rows.Sum(r => r.TotalTokens);
        long inputTokens = rows.Sum(r => r.InputTokens);
        long cachedTokens = rows.Sum(r => r.CachedInputTokens);
        long uncachedTokens = Math.Max(0, inputTokens - cachedTokens);
        double cache = inputTokens <= 0 ? 0 : 100d * cachedTokens / inputTokens;
        int compactions = rows.Sum(r => r.CompactionsInTurn);
        int lowCache = rows.Count(r => r.InputTokens >= 50_000 && r.CacheHitPercent < 50);
        efficiency.Text = $"{DeepTelemetry.Format(totalTokens)} tokens · {cache:0.#}% cached · {DeepTelemetry.Format(uncachedTokens)} uncached · {rows.Count:N0} requests · {compactions:N0} compactions · {lowCache:N0} low-cache flags";
    }

    void ExportCsv()
    {
        if (local == null || local.Requests.Count == 0) { MessageBox.Show(this, "No local request telemetry to export.", "Codex Usage"); return; }
        using var save = new SaveFileDialog { Filter = "CSV file (*.csv)|*.csv", FileName = $"codex-usage-{DateTime.Now:yyyy-MM-dd}.csv" };
        if (save.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            File.WriteAllText(save.FileName, DeepTelemetry.ExportCsv(local));
            MessageBox.Show(this, "Exported request-level usage metadata. Prompt and response text are not included.", "Codex Usage");
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Export failed", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
    }

    static TabPage Page(string text) => new(text) { BackColor = Theme.Background, ForeColor = Theme.Text, Padding = new Padding(6) };

    static DataGridView Grid()
    {
        var grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            BackgroundColor = Color.FromArgb(10, 20, 30),
            ForeColor = Theme.Text,
            GridColor = Color.FromArgb(38, 58, 74),
            BorderStyle = BorderStyle.None,
            ReadOnly = true,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false,
            RowHeadersVisible = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            MultiSelect = false,
            AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.None,
            ColumnHeadersHeight = 31,
            RowTemplate = { Height = 27 },
            EnableHeadersVisualStyles = false
        };
        grid.DefaultCellStyle.BackColor = Color.FromArgb(13, 27, 39);
        grid.DefaultCellStyle.ForeColor = Theme.Text;
        grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(28, 73, 94);
        grid.DefaultCellStyle.SelectionForeColor = Theme.Text;
        grid.DefaultCellStyle.Font = new Font("Segoe UI", 8.25f);
        grid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(19, 39, 55);
        grid.ColumnHeadersDefaultCellStyle.ForeColor = Theme.Muted;
        grid.ColumnHeadersDefaultCellStyle.SelectionBackColor = Color.FromArgb(19, 39, 55);
        grid.ColumnHeadersDefaultCellStyle.Font = new Font("Segoe UI", 8, FontStyle.Bold);
        return grid;
    }

    static void AddColumns(DataGridView grid, params (string Header, int Width)[] columns)
    {
        foreach (var (header, width) in columns)
            grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = header, Width = width, SortMode = DataGridViewColumnSortMode.Automatic });
    }

    static void AddRequestColumns(DataGridView g) => AddColumns(g,
        ("Time", 120), ("Project", 120), ("Model", 135), ("Effort", 70), ("Total", 78), ("Input", 78), ("Cached", 78), ("Cache %", 70),
        ("Uncached", 82), ("Cache write", 82), ("Output", 72), ("Reasoning", 78), ("Context %", 78), ("5h Δ", 65), ("Week Δ", 65), ("Compacts", 70), ("Thread", 85), ("Turn", 85));

    static void AddModelColumns(DataGridView g) => AddColumns(g,
        ("Model", 150), ("Effort", 75), ("Requests", 72), ("Total", 90), ("Avg/request", 90), ("Input", 90), ("Cached", 90), ("Cache %", 70), ("Uncached", 90),
        ("Output", 80), ("Reasoning", 85), ("Avg context", 85), ("Compacts", 72), ("5h % / 1M", 82), ("Week % / 1M", 90));

    static void AddSessionColumns(DataGridView g) => AddColumns(g,
        ("Project", 150), ("Source", 90), ("Models", 190), ("First", 105), ("Last", 105), ("Requests", 72), ("Total", 90), ("Cache %", 70), ("Peak context", 90), ("Compacts", 72), ("Thread", 95));

    static void AddReviewColumns(DataGridView g) => AddColumns(g,
        ("Severity", 70), ("Signal", 160), ("Why review it", 480), ("Project", 130), ("Time", 120), ("Tokens", 85), ("Thread", 90), ("Turn", 90));

    static string ShortId(string value) => string.IsNullOrWhiteSpace(value) ? "—" : value.Length <= 8 ? value : value[..8];
    static string ShortPath(string path)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return !string.IsNullOrWhiteSpace(home) && path.StartsWith(home, StringComparison.OrdinalIgnoreCase) ? "~" + path[home.Length..] : path;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        int dark = 1;
        Native.DwmSetWindowAttribute(Handle, 20, ref dark, sizeof(int));
        int corner = 2;
        Native.DwmSetWindowAttribute(Handle, 33, ref corner, sizeof(int));
        int backdrop = 2;
        Native.DwmSetWindowAttribute(Handle, 38, ref backdrop, sizeof(int));
    }
}

sealed class AnalyticsSurface : Control
{
    readonly string profileId;
    readonly string accountName;
    readonly Func<JsonNode?> tokenUsageProvider;
    List<UsageHistorySample> history = new();
    JsonNode? usage;

    public AnalyticsSurface(string profileId, string accountName, Func<JsonNode?> tokenUsageProvider)
    {
        this.profileId = profileId;
        this.accountName = accountName;
        this.tokenUsageProvider = tokenUsageProvider;
        DoubleBuffered = true;
        ResizeRedraw = true;
        BackColor = Theme.Background;
        ForeColor = Theme.Text;
    }

    public void RefreshData()
    {
        history = UsageHistoryStore.Load(profileId);
        usage = tokenUsageProvider()?.DeepClone();
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        Theme.Label(g, "Usage analytics", new Rectangle(22, 14, Width - 44, 28), 15, Theme.Text, true);
        Theme.Label(g, accountName, new Rectangle(22, 41, Width - 44, 20), 8.5f, Theme.Muted);

        var summary = usage?["summary"];
        long? lifetime = ReadLong(summary?["lifetimeTokens"]);
        long? peak = ReadLong(summary?["peakDailyTokens"]);
        int? streak = ReadInt(summary?["currentStreakDays"]);
        var recentTokenRate = TokenRatePerHour(history);
        var quotaRate = QuotaBurnPerHour(history, s => s.FiveHourRemaining);

        int gap = 10;
        int cardW = Math.Max(120, (Width - 44 - gap * 3) / 4);
        DrawMetric(g, new Rectangle(22, 72, cardW, 70), "LIFETIME TOKENS", FormatNumber(lifetime), Theme.Mint);
        DrawMetric(g, new Rectangle(22 + (cardW + gap), 72, cardW, 70), "PEAK DAY", FormatNumber(peak), Theme.Lilac);
        DrawMetric(g, new Rectangle(22 + (cardW + gap) * 2, 72, cardW, 70), "RECENT TOKENS / HR", recentTokenRate is { } tr ? FormatNumber((long)tr) : "—", Theme.Mint);
        DrawMetric(g, new Rectangle(22 + (cardW + gap) * 3, 72, cardW, 70), "5H BURN / HR", quotaRate is { } qr ? $"{qr:0.##}%" : "—", quotaRate > 10 ? Color.Salmon : Theme.Lilac);

        int chartLeft = 22;
        int chartWidth = Width - 44;
        int quotaTop = 164;
        int quotaHeight = Math.Max(125, (Height - 260) / 2);
        DrawQuotaChart(g, new Rectangle(chartLeft, quotaTop, chartWidth, quotaHeight), history);

        int tokenTop = quotaTop + quotaHeight + 24;
        int tokenHeight = Math.Max(125, Height - tokenTop - 48);
        DrawDailyTokens(g, new Rectangle(chartLeft, tokenTop, chartWidth, tokenHeight), usage?["dailyUsageBuckets"] as JsonArray);

        string note = history.Count < 2
            ? "Collecting local samples. Leave the widget running to build burn-rate history."
            : "Burn rates are observed estimates. Codex quota percentage is not a published 1:1 token conversion.";
        Theme.Label(g, note, new Rectangle(22, Height - 28, Width - 44, 18), 7.25f, Theme.Muted);
    }

    static void DrawMetric(Graphics g, Rectangle rect, string label, string value, Color accent)
    {
        using var path = Theme.Round(rect, 10);
        using var fill = new LinearGradientBrush(rect, Color.FromArgb(20, 39, 55), Color.FromArgb(13, 27, 39), 90f);
        using var edge = new Pen(Color.FromArgb(43, 67, 86));
        g.FillPath(fill, path);
        g.DrawPath(edge, path);
        Theme.Label(g, label, new Rectangle(rect.X + 11, rect.Y + 9, rect.Width - 22, 16), 7, Theme.Muted, true);
        Theme.Label(g, value, new Rectangle(rect.X + 11, rect.Y + 29, rect.Width - 22, 30), 14, accent, true);
    }

    static void DrawQuotaChart(Graphics g, Rectangle rect, List<UsageHistorySample> samples)
    {
        Theme.Label(g, "Quota remaining · last 24 hours", new Rectangle(rect.X, rect.Y - 20, rect.Width, 18), 8.5f, Theme.Text, true);
        DrawChartBackground(g, rect);
        var recent = samples.Where(s => s.TimestampUtc >= DateTime.UtcNow.AddHours(-24)).ToList();
        if (recent.Count < 2)
        {
            Theme.Label(g, "Waiting for at least two samples…", rect, 8, Theme.Muted);
            return;
        }
        DrawGrid(g, rect, 0, 100, v => $"{v:0}%");
        DrawHistoryLine(g, rect, recent, s => s.FiveHourRemaining, Theme.Mint);
        DrawHistoryLine(g, rect, recent, s => s.WeeklyRemaining, Theme.Lilac);

        using var mint = new SolidBrush(Theme.Mint);
        using var lilac = new SolidBrush(Theme.Lilac);
        g.FillEllipse(mint, rect.Right - 171, rect.Y + 9, 7, 7);
        Theme.Label(g, "5-hour", new Rectangle(rect.Right - 158, rect.Y + 3, 58, 18), 7.25f, Theme.Muted);
        g.FillEllipse(lilac, rect.Right - 94, rect.Y + 9, 7, 7);
        Theme.Label(g, "Weekly", new Rectangle(rect.Right - 81, rect.Y + 3, 58, 18), 7.25f, Theme.Muted);
    }

    static void DrawDailyTokens(Graphics g, Rectangle rect, JsonArray? buckets)
    {
        Theme.Label(g, "Account token activity · recent days", new Rectangle(rect.X, rect.Y - 20, rect.Width, 18), 8.5f, Theme.Text, true);
        DrawChartBackground(g, rect);
        var rows = new List<(DateTime Date, long Tokens)>();
        if (buckets != null)
        {
            foreach (var node in buckets)
            {
                if (DateTime.TryParse(node?["startDate"]?.ToString(), out var date) && ReadLong(node?["tokens"]) is { } tokens)
                    rows.Add((date.Date, tokens));
            }
        }
        rows = rows.OrderBy(x => x.Date).TakeLast(14).ToList();
        if (rows.Count == 0)
        {
            Theme.Label(g, "Token-activity history is not available for this account yet.", rect, 8, Theme.Muted);
            return;
        }
        long max = Math.Max(1, rows.Max(x => x.Tokens));
        int innerLeft = rect.X + 42, innerRight = rect.Right - 12, innerTop = rect.Y + 28, innerBottom = rect.Bottom - 26;
        int width = innerRight - innerLeft, height = innerBottom - innerTop;
        float slot = width / (float)rows.Count;
        for (int i = 0; i < rows.Count; i++)
        {
            float h = Math.Max(1, height * rows[i].Tokens / (float)max);
            var bar = new RectangleF(innerLeft + i * slot + slot * .18f, innerBottom - h, Math.Max(2, slot * .64f), h);
            using var brush = new LinearGradientBrush(bar, Theme.Mint, Theme.Lilac, 90f);
            using var shape = Theme.Round(bar, Math.Min(4, bar.Width / 2));
            g.FillPath(brush, shape);
            if (rows.Count <= 10 || i % 2 == 0)
                Theme.Label(g, rows[i].Date.ToString("M/d"), new Rectangle((int)(innerLeft + i * slot), innerBottom + 3, (int)Math.Ceiling(slot), 16), 6.5f, Theme.Muted);
        }
        Theme.Label(g, FormatNumber(max), new Rectangle(rect.X + 5, innerTop - 8, 34, 16), 6.5f, Theme.Muted, false, true);
        Theme.Label(g, "0", new Rectangle(rect.X + 5, innerBottom - 8, 34, 16), 6.5f, Theme.Muted, false, true);
    }

    static void DrawChartBackground(Graphics g, Rectangle rect)
    {
        using var path = Theme.Round(rect, 11);
        using var fill = new SolidBrush(Color.FromArgb(13, 27, 39));
        using var edge = new Pen(Color.FromArgb(40, 63, 82));
        g.FillPath(fill, path);
        g.DrawPath(edge, path);
    }

    static void DrawGrid(Graphics g, Rectangle rect, double min, double max, Func<double, string> label)
    {
        int left = rect.X + 42, right = rect.Right - 12, top = rect.Y + 28, bottom = rect.Bottom - 20;
        using var pen = new Pen(Color.FromArgb(29, 49, 64));
        for (int i = 0; i <= 4; i++)
        {
            float y = top + (bottom - top) * i / 4f;
            g.DrawLine(pen, left, y, right, y);
            double value = max - (max - min) * i / 4;
            Theme.Label(g, label(value), new Rectangle(rect.X + 4, (int)y - 8, 34, 16), 6.5f, Theme.Muted, false, true);
        }
    }

    static void DrawHistoryLine(Graphics g, Rectangle rect, List<UsageHistorySample> samples, Func<UsageHistorySample, double?> selector, Color color)
    {
        var points = samples.Where(s => selector(s) != null).ToList();
        if (points.Count < 2) return;
        int left = rect.X + 42, right = rect.Right - 12, top = rect.Y + 28, bottom = rect.Bottom - 20;
        var start = samples.First().TimestampUtc;
        var end = samples.Last().TimestampUtc;
        double totalSeconds = Math.Max(1, (end - start).TotalSeconds);
        using var pen = new Pen(color, 2f) { LineJoin = LineJoin.Round };
        PointF? previous = null;
        foreach (var s in points)
        {
            double v = Math.Clamp(selector(s)!.Value, 0, 100);
            float x = left + (float)((s.TimestampUtc - start).TotalSeconds / totalSeconds) * (right - left);
            float y = bottom - (float)(v / 100) * (bottom - top);
            var point = new PointF(x, y);
            if (previous != null) g.DrawLine(pen, previous.Value, point);
            previous = point;
        }
    }

    static double? QuotaBurnPerHour(List<UsageHistorySample> samples, Func<UsageHistorySample, double?> selector)
    {
        var rows = samples.Where(s => s.TimestampUtc >= DateTime.UtcNow.AddHours(-6) && selector(s) != null).ToList();
        if (rows.Count < 2) return null;
        int start = 0;
        for (int i = 1; i < rows.Count; i++)
            if (selector(rows[i])!.Value > selector(rows[i - 1])!.Value + 1) start = i;
        var segment = rows.Skip(start).ToList();
        if (segment.Count < 2) return null;
        double hours = (segment[^1].TimestampUtc - segment[0].TimestampUtc).TotalHours;
        if (hours < .05) return null;
        double delta = selector(segment[0])!.Value - selector(segment[^1])!.Value;
        return delta > 0 ? delta / hours : 0;
    }

    static double? TokenRatePerHour(List<UsageHistorySample> samples)
    {
        var rows = samples.Where(s => s.TimestampUtc >= DateTime.UtcNow.AddHours(-6) && s.LifetimeTokens != null).ToList();
        if (rows.Count < 2) return null;
        int start = 0;
        for (int i = 1; i < rows.Count; i++)
            if (rows[i].LifetimeTokens < rows[i - 1].LifetimeTokens) start = i;
        var segment = rows.Skip(start).ToList();
        if (segment.Count < 2) return null;
        double hours = (segment[^1].TimestampUtc - segment[0].TimestampUtc).TotalHours;
        if (hours < .05) return null;
        long delta = segment[^1].LifetimeTokens!.Value - segment[0].LifetimeTokens!.Value;
        return Math.Max(0, delta / hours);
    }

    static long? ReadLong(JsonNode? node)
    {
        if (node is not JsonValue v) return null;
        if (v.TryGetValue<long>(out var n)) return n;
        if (v.TryGetValue<double>(out var d) && double.IsFinite(d)) return (long)d;
        return null;
    }

    static int? ReadInt(JsonNode? node)
    {
        if (node is not JsonValue v) return null;
        if (v.TryGetValue<int>(out var n)) return n;
        if (v.TryGetValue<long>(out var l)) return (int)Math.Clamp(l, int.MinValue, int.MaxValue);
        return null;
    }

    static string FormatNumber(long? value)
    {
        if (value is null) return "—";
        double n = value.Value;
        if (n >= 1_000_000_000) return $"{n / 1_000_000_000:0.##}B";
        if (n >= 1_000_000) return $"{n / 1_000_000:0.##}M";
        if (n >= 1_000) return $"{n / 1_000:0.#}K";
        return value.Value.ToString("N0");
    }
}
