using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CodexUsageWidget;

sealed class LocalRequestUsage
{
    public DateTime TimestampUtc { get; set; }
    public string ThreadId { get; set; } = "";
    public string TurnId { get; set; } = "";
    public string Project { get; set; } = "";
    public string Source { get; set; } = "";
    public string Model { get; set; } = "unknown";
    public string Effort { get; set; } = "unknown";
    public long InputTokens { get; set; }
    public long CachedInputTokens { get; set; }
    public long CacheWriteInputTokens { get; set; }
    public long OutputTokens { get; set; }
    public long ReasoningOutputTokens { get; set; }
    public long TotalTokens { get; set; }
    public long? ContextWindow { get; set; }
    public double? FiveHourUsedPercent { get; set; }
    public double? WeeklyUsedPercent { get; set; }
    public double? FiveHourDelta { get; set; }
    public double? WeeklyDelta { get; set; }
    public int CompactionsInTurn { get; set; }

    public long UncachedInputTokens => Math.Max(0, InputTokens - CachedInputTokens);
    public double CacheHitPercent => InputTokens <= 0 ? 0 : 100d * CachedInputTokens / InputTokens;
    public double? ContextPercent => ContextWindow is > 0 ? 100d * TotalTokens / ContextWindow.Value : null;
    public double ReasoningSharePercent
    {
        get
        {
            var denom = OutputTokens + ReasoningOutputTokens;
            return denom <= 0 ? 0 : 100d * ReasoningOutputTokens / denom;
        }
    }
}

sealed record LocalModelSummary(
    string Model,
    string Effort,
    int Requests,
    long TotalTokens,
    long InputTokens,
    long CachedInputTokens,
    long UncachedInputTokens,
    long OutputTokens,
    long ReasoningTokens,
    double CacheHitPercent,
    double AverageTokensPerRequest,
    double? AverageContextPercent,
    int Compactions,
    double? FiveHourPercentPerMillionTokens,
    double? WeeklyPercentPerMillionTokens);

sealed record LocalSessionSummary(
    string ThreadId,
    string Project,
    string Source,
    string Models,
    DateTime FirstUtc,
    DateTime LastUtc,
    int Requests,
    long TotalTokens,
    double CacheHitPercent,
    double? PeakContextPercent,
    int Compactions);

sealed record UsageReviewFlag(
    string Severity,
    string Signal,
    string Detail,
    string ThreadId,
    string TurnId,
    DateTime TimestampUtc,
    long Tokens);

sealed class DeepTelemetrySnapshot
{
    public List<LocalRequestUsage> Requests { get; } = new();
    public List<LocalModelSummary> Models { get; } = new();
    public List<LocalSessionSummary> Sessions { get; } = new();
    public List<UsageReviewFlag> Flags { get; } = new();
    public List<string> ScannedRoots { get; } = new();
    public int FilesScanned { get; set; }
    public DateTime ScannedAtUtc { get; set; } = DateTime.UtcNow;
    public string? Warning { get; set; }
}

static class DeepTelemetry
{
    sealed record TurnContextInfo(string Model, string Effort);
    sealed record RateSnapshot(double? FiveHourUsed, double? WeeklyUsed);

    static readonly Regex ThreadIdPattern = new(@"([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})", RegexOptions.Compiled);
    static readonly object CacheGate = new();
    static DeepTelemetrySnapshot? cached;
    static DateTime cacheTimeUtc;

    public static Task<DeepTelemetrySnapshot> ScanAsync(string profileId, bool force = false) =>
        Task.Run(() => Scan(profileId, force));

    public static DeepTelemetrySnapshot Scan(string profileId, bool force = false)
    {
        lock (CacheGate)
        {
            if (!force && cached != null && DateTime.UtcNow - cacheTimeUtc < TimeSpan.FromSeconds(20))
                return cached;
        }

        var snapshot = new DeepTelemetrySnapshot();
        var roots = CandidateRoots(profileId).Distinct(StringComparer.OrdinalIgnoreCase).Where(Directory.Exists).ToList();
        snapshot.ScannedRoots.AddRange(roots);

        var files = new List<string>();
        foreach (var root in roots)
        {
            try
            {
                files.AddRange(Directory.EnumerateFiles(root, "rollout-*.jsonl", SearchOption.AllDirectories)
                    .Where(path =>
                    {
                        try { return File.GetLastWriteTimeUtc(path) >= DateTime.UtcNow.AddDays(-30); }
                        catch { return false; }
                    }));
            }
            catch { }
        }

        files = files.Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(path => { try { return File.GetLastWriteTimeUtc(path); } catch { return DateTime.MinValue; } })
            .Take(1500)
            .ToList();

        foreach (var file in files)
        {
            try
            {
                ScanFile(file, snapshot);
                snapshot.FilesScanned++;
            }
            catch { }
        }

        snapshot.Requests.Sort((a, b) => b.TimestampUtc.CompareTo(a.TimestampUtc));
        ComputeQuotaDeltas(snapshot.Requests);
        BuildAggregates(snapshot);

        if (snapshot.ScannedRoots.Count == 0)
            snapshot.Warning = "No local Codex session directory was found on this PC.";
        else if (snapshot.Requests.Count == 0)
            snapshot.Warning = "No token-usage records were found in recent local Codex sessions. Account-wide daily totals can still appear in Overview.";

        lock (CacheGate)
        {
            cached = snapshot;
            cacheTimeUtc = DateTime.UtcNow;
        }
        return snapshot;
    }

    static IEnumerable<string> CandidateRoots(string profileId)
    {
        var userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(userHome))
        {
            yield return Path.Combine(userHome, ".codex", "sessions");
            yield return Path.Combine(userHome, ".codex", "archived_sessions");
        }

        var envHome = Environment.GetEnvironmentVariable("CODEX_HOME");
        if (!string.IsNullOrWhiteSpace(envHome))
        {
            yield return Path.Combine(envHome, "sessions");
            yield return Path.Combine(envHome, "archived_sessions");
        }

        var isolated = Path.Combine(Widget.DataRoot, "profiles", profileId);
        yield return Path.Combine(isolated, "sessions");
        yield return Path.Combine(isolated, "archived_sessions");
    }

    static void ScanFile(string file, DeepTelemetrySnapshot snapshot)
    {
        string threadId = ThreadIdPattern.Match(Path.GetFileName(file)).Groups[1].Value;
        string project = "";
        string source = "";
        string currentModel = "unknown";
        string currentEffort = "unknown";
        var turnContexts = new Dictionary<string, TurnContextInfo>(StringComparer.Ordinal);
        var compactByTurn = new Dictionary<string, int>(StringComparer.Ordinal);
        var explicitRows = new List<LocalRequestUsage>();
        var fallbackRows = new List<LocalRequestUsage>();
        LocalRequestUsage? lastExplicit = null;
        LocalRequestUsage? lastFallback = null;
        var fallbackKeys = new HashSet<string>(StringComparer.Ordinal);

        foreach (var line in File.ReadLines(file))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            JsonNode? root;
            try { root = JsonNode.Parse(line); }
            catch { continue; }
            if (root == null) continue;

            var type = Text(root["type"]);
            var payload = root["payload"];
            var ts = ReadTimestamp(root["timestamp"]) ?? File.GetLastWriteTimeUtc(file);

            if (type == "session_meta")
            {
                threadId = FirstText(payload?["id"], payload?["thread_id"], payload?["threadId"]) ?? threadId;
                var cwd = FirstText(payload?["cwd"], payload?["working_directory"]);
                if (!string.IsNullOrWhiteSpace(cwd))
                {
                    try
                    {
                        var trimmed = cwd.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                        project = Path.GetFileName(trimmed);
                        if (string.IsNullOrWhiteSpace(project)) project = trimmed;
                    }
                    catch { project = cwd; }
                }
                source = SourceText(payload?["source"]) ?? FirstText(payload?["originator"], payload?["client"]) ?? source;
                currentModel = FirstText(payload?["model"], payload?["model_name"]) ?? currentModel;
                currentEffort = FirstText(payload?["reasoning_effort"], payload?["effort"]) ?? currentEffort;
                continue;
            }

            if (type == "thread_settings_applied")
            {
                currentModel = FirstText(payload?["model"], payload?["model_name"]) ?? currentModel;
                currentEffort = FirstText(payload?["reasoning_effort"], payload?["effort"]) ?? currentEffort;
                continue;
            }

            if (type == "turn_context")
            {
                var turn = FirstText(payload?["turn_id"], payload?["turnId"]) ?? "";
                var model = FirstText(payload?["model"], payload?["model_name"]) ?? currentModel;
                var effort = FirstText(payload?["effort"], payload?["reasoning_effort"]) ?? currentEffort;
                currentModel = model;
                currentEffort = effort;
                if (turn.Length > 0) turnContexts[turn] = new TurnContextInfo(model, effort);
                continue;
            }

            if (type == "compacted" || (type == "event_msg" && Text(payload?["type"]) == "context_compacted"))
            {
                var turn = FirstText(payload?["turn_id"], payload?["turnId"]) ?? "";
                if (turn.Length > 0) compactByTurn[turn] = compactByTurn.GetValueOrDefault(turn) + 1;
                continue;
            }

            if (type == "token_usage_record")
            {
                var usage = payload?["usage"];
                if (usage == null) continue;
                var turn = FirstText(payload?["turn_id"], payload?["turnId"]) ?? "";
                var context = turnContexts.TryGetValue(turn, out var tc) ? tc : new TurnContextInfo(currentModel, currentEffort);
                var row = BuildRow(ts, threadId, turn, project, source, context.Model, context.Effort, usage, ReadLong(payload?["model_context_window"]));
                explicitRows.Add(row);
                lastExplicit = row;
                continue;
            }

            bool selected = type == "selected_token_count";
            bool tokenEvent = type == "event_msg" && Text(payload?["type"]) == "token_count";
            if (!selected && !tokenEvent) continue;

            var info = selected ? payload?["info"] : payload?["info"];
            var lastUsage = info?["last_token_usage"];
            if (lastUsage == null) continue;
            var turnId = FirstText(payload?["turn_id"], payload?["turnId"], info?["turn_id"]) ?? "";
            var ctx = turnContexts.TryGetValue(turnId, out var found) ? found : new TurnContextInfo(currentModel, currentEffort);
            var contextWindow = ReadLong(info?["model_context_window"]);
            var rate = ParseRateSnapshot(payload?["rate_limits"] ?? info?["rate_limits"]);

            if (selected)
            {
                if (lastExplicit != null && SameUsage(lastExplicit, lastUsage))
                    ApplyRate(lastExplicit, rate);
                if (lastFallback != null && SameUsage(lastFallback, lastUsage))
                    ApplyRate(lastFallback, rate);
                continue;
            }

            var key = $"{turnId}|{ReadLong(lastUsage["input_tokens"])}|{ReadLong(lastUsage["cached_input_tokens"])}|{ReadLong(lastUsage["output_tokens"])}|{ReadLong(lastUsage["reasoning_output_tokens"])}|{ReadLong(lastUsage["total_tokens"])}";
            if (!fallbackKeys.Add(key)) continue;
            var fallback = BuildRow(ts, threadId, turnId, project, source, ctx.Model, ctx.Effort, lastUsage, contextWindow);
            ApplyRate(fallback, rate);
            fallbackRows.Add(fallback);
            lastFallback = fallback;
        }

        var chosen = explicitRows.Count > 0 ? explicitRows : fallbackRows;
        foreach (var row in chosen)
        {
            if (string.IsNullOrWhiteSpace(row.ThreadId)) row.ThreadId = ThreadIdPattern.Match(Path.GetFileName(file)).Groups[1].Value;
            if (turnContexts.TryGetValue(row.TurnId, out var tc))
            {
                row.Model = tc.Model;
                row.Effort = tc.Effort;
            }
            row.CompactionsInTurn = compactByTurn.GetValueOrDefault(row.TurnId);
            snapshot.Requests.Add(row);
        }
    }

    static LocalRequestUsage BuildRow(DateTime ts, string threadId, string turnId, string project, string source, string model, string effort, JsonNode usage, long? contextWindow)
    {
        return new LocalRequestUsage
        {
            TimestampUtc = DateTime.SpecifyKind(ts, DateTimeKind.Utc),
            ThreadId = threadId,
            TurnId = turnId,
            Project = project,
            Source = source,
            Model = string.IsNullOrWhiteSpace(model) ? "unknown" : model,
            Effort = string.IsNullOrWhiteSpace(effort) ? "unknown" : effort,
            InputTokens = ReadLong(usage["input_tokens"]) ?? 0,
            CachedInputTokens = ReadLong(usage["cached_input_tokens"]) ?? 0,
            CacheWriteInputTokens = ReadLong(usage["cache_write_input_tokens"]) ?? 0,
            OutputTokens = ReadLong(usage["output_tokens"]) ?? 0,
            ReasoningOutputTokens = ReadLong(usage["reasoning_output_tokens"]) ?? 0,
            TotalTokens = ReadLong(usage["total_tokens"]) ?? 0,
            ContextWindow = contextWindow
        };
    }

    static bool SameUsage(LocalRequestUsage row, JsonNode usage) =>
        row.InputTokens == (ReadLong(usage["input_tokens"]) ?? 0) &&
        row.CachedInputTokens == (ReadLong(usage["cached_input_tokens"]) ?? 0) &&
        row.OutputTokens == (ReadLong(usage["output_tokens"]) ?? 0) &&
        row.ReasoningOutputTokens == (ReadLong(usage["reasoning_output_tokens"]) ?? 0) &&
        row.TotalTokens == (ReadLong(usage["total_tokens"]) ?? 0);

    static RateSnapshot ParseRateSnapshot(JsonNode? rate)
    {
        if (rate == null) return new RateSnapshot(null, null);
        double? five = null, weekly = null;
        foreach (var name in new[] { "primary", "secondary" })
        {
            var w = rate[name];
            if (w == null) continue;
            var mins = ReadDouble(w["window_minutes"] ?? w["windowDurationMins"]);
            var used = ReadDouble(w["used_percent"] ?? w["usedPercent"]);
            if (mins == 300) five = used;
            else if (mins == 10080) weekly = used;
        }
        return new RateSnapshot(five, weekly);
    }

    static void ApplyRate(LocalRequestUsage row, RateSnapshot rate)
    {
        if (rate.FiveHourUsed != null) row.FiveHourUsedPercent = rate.FiveHourUsed;
        if (rate.WeeklyUsed != null) row.WeeklyUsedPercent = rate.WeeklyUsed;
    }

    static void ComputeQuotaDeltas(List<LocalRequestUsage> rows)
    {
        var chronological = rows.OrderBy(r => r.TimestampUtc).ToList();
        double? lastFive = null, lastWeek = null;
        foreach (var row in chronological)
        {
            if (row.FiveHourUsedPercent is { } five)
            {
                if (lastFive is { } prev && five >= prev && five - prev <= 50) row.FiveHourDelta = five - prev;
                lastFive = five;
            }
            if (row.WeeklyUsedPercent is { } week)
            {
                if (lastWeek is { } prev && week >= prev && week - prev <= 50) row.WeeklyDelta = week - prev;
                lastWeek = week;
            }
        }
    }

    static void BuildAggregates(DeepTelemetrySnapshot snapshot)
    {
        foreach (var g in snapshot.Requests.GroupBy(r => (r.Model, r.Effort)).OrderByDescending(g => g.Sum(r => r.TotalTokens)))
        {
            var input = g.Sum(r => r.InputTokens);
            var cachedInput = g.Sum(r => r.CachedInputTokens);
            var measuredFive = g.Where(r => r.FiveHourDelta != null).ToList();
            var measuredWeek = g.Where(r => r.WeeklyDelta != null).ToList();
            snapshot.Models.Add(new LocalModelSummary(
                g.Key.Model,
                g.Key.Effort,
                g.Count(),
                g.Sum(r => r.TotalTokens),
                input,
                cachedInput,
                Math.Max(0, input - cachedInput),
                g.Sum(r => r.OutputTokens),
                g.Sum(r => r.ReasoningOutputTokens),
                input <= 0 ? 0 : 100d * cachedInput / input,
                g.Average(r => (double)r.TotalTokens),
                AverageNullable(g.Select(r => r.ContextPercent)),
                g.Sum(r => r.CompactionsInTurn),
                QuotaPerMillion(measuredFive, true),
                QuotaPerMillion(measuredWeek, false)));
        }

        foreach (var g in snapshot.Requests.GroupBy(r => r.ThreadId).OrderByDescending(g => g.Sum(r => r.TotalTokens)))
        {
            var input = g.Sum(r => r.InputTokens);
            var cachedInput = g.Sum(r => r.CachedInputTokens);
            snapshot.Sessions.Add(new LocalSessionSummary(
                g.Key,
                g.Select(r => r.Project).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? "",
                g.Select(r => r.Source).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? "",
                string.Join(", ", g.Select(r => r.Model).Distinct().Take(3)),
                g.Min(r => r.TimestampUtc),
                g.Max(r => r.TimestampUtc),
                g.Count(),
                g.Sum(r => r.TotalTokens),
                input <= 0 ? 0 : 100d * cachedInput / input,
                g.Where(r => r.ContextPercent != null).Select(r => r.ContextPercent!.Value).DefaultIfEmpty().Max() is var peak && peak > 0 ? peak : null,
                g.Sum(r => r.CompactionsInTurn)));
        }

        BuildFlags(snapshot);
    }

    static void BuildFlags(DeepTelemetrySnapshot snapshot)
    {
        foreach (var row in snapshot.Requests)
        {
            if (row.InputTokens >= 50_000 && row.CacheHitPercent < 50)
                snapshot.Flags.Add(new UsageReviewFlag("High", "Low cache reuse", $"{row.Model} {row.Effort}: {row.CacheHitPercent:0.#}% cache hit on {Format(row.InputTokens)} input tokens", row.ThreadId, row.TurnId, row.TimestampUtc, row.TotalTokens));

            if (row.UncachedInputTokens >= 75_000)
                snapshot.Flags.Add(new UsageReviewFlag("High", "Large uncached input", $"{Format(row.UncachedInputTokens)} uncached input tokens in one model request", row.ThreadId, row.TurnId, row.TimestampUtc, row.TotalTokens));

            if (row.ContextPercent is { } cp && cp >= 75)
                snapshot.Flags.Add(new UsageReviewFlag(cp >= 90 ? "High" : "Medium", "Context pressure", $"Request used about {cp:0.#}% of the reported context window", row.ThreadId, row.TurnId, row.TimestampUtc, row.TotalTokens));

            if (row.ReasoningOutputTokens >= 10_000 || row.ReasoningSharePercent >= 70 && row.ReasoningOutputTokens >= 2_000)
                snapshot.Flags.Add(new UsageReviewFlag("Medium", "Reasoning-heavy request", $"{Format(row.ReasoningOutputTokens)} reasoning tokens ({row.ReasoningSharePercent:0.#}% of generated+reasoning tokens)", row.ThreadId, row.TurnId, row.TimestampUtc, row.TotalTokens));

            if (row.InputTokens >= 100_000 && row.OutputTokens + row.ReasoningOutputTokens < 1_000)
                snapshot.Flags.Add(new UsageReviewFlag("Medium", "High context / tiny result", $"{Format(row.InputTokens)} input tokens for only {Format(row.OutputTokens + row.ReasoningOutputTokens)} generated+reasoning tokens", row.ThreadId, row.TurnId, row.TimestampUtc, row.TotalTokens));
        }

        foreach (var g in snapshot.Requests.Where(r => !string.IsNullOrWhiteSpace(r.TurnId)).GroupBy(r => r.TurnId))
        {
            if (g.Count() >= 5)
            {
                var last = g.MaxBy(r => r.TimestampUtc)!;
                snapshot.Flags.Add(new UsageReviewFlag(g.Count() >= 10 ? "High" : "Medium", "Many model calls in one turn", $"{g.Count()} model requests consumed {Format(g.Sum(r => r.TotalTokens))} tokens in one turn", last.ThreadId, g.Key, last.TimestampUtc, g.Sum(r => r.TotalTokens)));
            }

            var compactions = g.Max(r => r.CompactionsInTurn);
            if (compactions > 0)
            {
                var last = g.MaxBy(r => r.TimestampUtc)!;
                snapshot.Flags.Add(new UsageReviewFlag(compactions >= 2 ? "High" : "Medium", "Context compaction", $"{compactions} compaction event(s) associated with this turn", last.ThreadId, g.Key, last.TimestampUtc, g.Sum(r => r.TotalTokens)));
            }
        }

        snapshot.Flags.Sort((a, b) =>
        {
            int Severity(string s) => s == "High" ? 0 : s == "Medium" ? 1 : 2;
            var c = Severity(a.Severity).CompareTo(Severity(b.Severity));
            return c != 0 ? c : b.Tokens.CompareTo(a.Tokens);
        });
    }

    static double? QuotaPerMillion(List<LocalRequestUsage> rows, bool five)
    {
        if (rows.Count == 0) return null;
        var tokenSum = rows.Sum(r => Math.Max(0, r.TotalTokens));
        var quota = rows.Sum(r => five ? r.FiveHourDelta ?? 0 : r.WeeklyDelta ?? 0);
        return tokenSum <= 0 || quota <= 0 ? null : quota / (tokenSum / 1_000_000d);
    }

    static double? AverageNullable(IEnumerable<double?> values)
    {
        var rows = values.Where(v => v != null).Select(v => v!.Value).ToList();
        return rows.Count == 0 ? null : rows.Average();
    }

    static DateTime? ReadTimestamp(JsonNode? node)
    {
        if (node == null) return null;
        if (DateTimeOffset.TryParse(node.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dto))
            return dto.UtcDateTime;
        return null;
    }

    static long? ReadLong(JsonNode? node)
    {
        if (node is not JsonValue v) return null;
        if (v.TryGetValue<long>(out var l)) return l;
        if (v.TryGetValue<int>(out var i)) return i;
        if (v.TryGetValue<double>(out var d) && double.IsFinite(d)) return (long)d;
        return long.TryParse(node.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
    }

    static double? ReadDouble(JsonNode? node)
    {
        if (node is not JsonValue v) return null;
        if (v.TryGetValue<double>(out var d) && double.IsFinite(d)) return d;
        if (v.TryGetValue<long>(out var l)) return l;
        return double.TryParse(node.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
    }

    static string? Text(JsonNode? node) => node?.ToString();
    static string? FirstText(params JsonNode?[] values) => values.Select(Text).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

    static string? SourceText(JsonNode? source)
    {
        if (source == null) return null;
        if (source is JsonValue) return source.ToString();
        if (source is JsonObject obj)
        {
            foreach (var key in new[] { "type", "kind", "originator", "name" })
                if (!string.IsNullOrWhiteSpace(obj[key]?.ToString())) return obj[key]!.ToString();
        }
        return null;
    }

    public static string Format(long value)
    {
        double n = value;
        if (n >= 1_000_000_000) return $"{n / 1_000_000_000:0.##}B";
        if (n >= 1_000_000) return $"{n / 1_000_000:0.##}M";
        if (n >= 1_000) return $"{n / 1_000:0.#}K";
        return value.ToString("N0");
    }

    public static string ExportCsv(DeepTelemetrySnapshot snapshot)
    {
        static string Q(string? s) => "\"" + (s ?? "").Replace("\"", "\"\"") + "\"";
        var sb = new StringBuilder();
        sb.AppendLine("timestamp_local,project,thread_id,turn_id,model,effort,input_tokens,cached_input_tokens,uncached_input_tokens,cache_write_input_tokens,output_tokens,reasoning_output_tokens,total_tokens,cache_hit_percent,context_percent,context_window,5h_quota_delta,weekly_quota_delta,compactions");
        foreach (var r in snapshot.Requests.OrderBy(r => r.TimestampUtc))
        {
            sb.Append(r.TimestampUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)).Append(',')
              .Append(Q(r.Project)).Append(',').Append(Q(r.ThreadId)).Append(',').Append(Q(r.TurnId)).Append(',')
              .Append(Q(r.Model)).Append(',').Append(Q(r.Effort)).Append(',')
              .Append(r.InputTokens).Append(',').Append(r.CachedInputTokens).Append(',').Append(r.UncachedInputTokens).Append(',')
              .Append(r.CacheWriteInputTokens).Append(',').Append(r.OutputTokens).Append(',').Append(r.ReasoningOutputTokens).Append(',')
              .Append(r.TotalTokens).Append(',').Append(r.CacheHitPercent.ToString("0.###", CultureInfo.InvariantCulture)).Append(',')
              .Append(r.ContextPercent?.ToString("0.###", CultureInfo.InvariantCulture) ?? "").Append(',')
              .Append(r.ContextWindow?.ToString(CultureInfo.InvariantCulture) ?? "").Append(',')
              .Append(r.FiveHourDelta?.ToString("0.###", CultureInfo.InvariantCulture) ?? "").Append(',')
              .Append(r.WeeklyDelta?.ToString("0.###", CultureInfo.InvariantCulture) ?? "").Append(',')
              .Append(r.CompactionsInTurn).AppendLine();
        }
        return sb.ToString();
    }
}
