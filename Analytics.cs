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
    readonly AnalyticsSurface surface;
    readonly System.Windows.Forms.Timer timer = new() { Interval = 5000 };

    public AnalyticsForm(string profileId, string accountName, Func<JsonNode?> tokenUsageProvider)
    {
        Text = "Codex Usage Analytics";
        BackColor = Theme.Background;
        ForeColor = Theme.Text;
        Font = new Font("Segoe UI", 9);
        MinimumSize = new Size(680, 500);
        ClientSize = new Size(820, 590);
        StartPosition = FormStartPosition.CenterParent;
        surface = new AnalyticsSurface(profileId, accountName, tokenUsageProvider) { Dock = DockStyle.Fill };
        Controls.Add(surface);
        timer.Tick += (_, _) => surface.RefreshData();
        Shown += (_, _) => { surface.RefreshData(); timer.Start(); };
        FormClosed += (_, _) => timer.Dispose();
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
