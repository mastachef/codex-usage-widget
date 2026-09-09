using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json.Nodes;

namespace CodexUsageWidget;

sealed class Rpc : IDisposable
{
    readonly Process process;
    readonly ConcurrentDictionary<int, TaskCompletionSource<JsonNode>> pending = new();
    readonly SemaphoreSlim writeLock = new(1);
    int nextId;
    public event Action<string, JsonNode?>? Notification;
    Rpc(Process p) { process = p; _ = Read(); _ = DrainErrors(); }
    public static string FindCodex()
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenAI", "Codex", "bin");
        if (Directory.Exists(root))
        {
            var found = Directory.GetFiles(root, "codex.exe", SearchOption.AllDirectories).OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
            if (found != null) return found;
        }
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            if (File.Exists(Path.Combine(dir, "codex.exe"))) return Path.Combine(dir, "codex.exe");
        throw new InvalidOperationException("Install Codex Desktop, then reopen this widget.");
    }
    public static async Task<Rpc> Start(string home)
    {
        Directory.CreateDirectory(home);
        var si = new ProcessStartInfo(FindCodex()) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = home };
        foreach (var arg in new[] { "app-server", "--stdio", "-c", "cli_auth_credentials_store=\"keyring\"" }) si.ArgumentList.Add(arg);
        si.Environment["CODEX_HOME"] = home;
        si.Environment.Remove("OPENAI_API_KEY");
        si.Environment.Remove("CODEX_API_KEY");
        var rpc = new Rpc(Process.Start(si) ?? throw new Exception("Unable to start Codex."));
        try
        {
            await rpc.Call("initialize", new { clientInfo = new { name = "codex_usage_widget", title = "Codex Usage Widget", version = "1.0.0" } });
            await rpc.Send(new { method = "initialized" });
            return rpc;
        }
        catch { rpc.Dispose(); throw; }
    }
    async Task DrainErrors() { try { while (await process.StandardError.ReadLineAsync() != null) { } } catch { } }
    async Task Read()
    {
        try
        {
            while (await process.StandardOutput.ReadLineAsync() is { } line)
            {
                JsonNode? msg;
                try { msg = JsonNode.Parse(line); } catch { continue; }
                if (msg?["id"] is JsonValue id && id.TryGetValue<int>(out var n) && pending.TryRemove(n, out var tcs))
                {
                    if (msg["error"] != null) tcs.TrySetException(new Exception(msg["error"]?["message"]?.ToString() ?? "Codex request failed."));
                    else tcs.TrySetResult(msg["result"]?.DeepClone() ?? new JsonObject());
                }
                else if (msg?["method"] is { } method) Notification?.Invoke(method.ToString(), msg["params"]?.DeepClone());
            }
        }
        catch { }
        finally { foreach (var p in pending.Values) p.TrySetException(new IOException("Codex disconnected. Press Refresh to reconnect.")); }
    }
    async Task Send(object obj)
    {
        await writeLock.WaitAsync();
        try { await process.StandardInput.WriteLineAsync(System.Text.Json.JsonSerializer.Serialize(obj)); await process.StandardInput.FlushAsync(); }
        finally { writeLock.Release(); }
    }
    public async Task<JsonNode> Call(string method, object? args = null)
    {
        var id = Interlocked.Increment(ref nextId);
        var tcs = new TaskCompletionSource<JsonNode>(TaskCreationOptions.RunContinuationsAsynchronously);
        pending[id] = tcs;
        try { await Send(new { id, method, @params = args }); return await tcs.Task.WaitAsync(TimeSpan.FromSeconds(45)); }
        finally { pending.TryRemove(id, out _); }
    }
    public void Dispose() { try { if (!process.HasExited) process.Kill(true); } catch { } process.Dispose(); }
}

static class Usage
{
    public static double? Remaining(JsonNode? w) => w?["usedPercent"] is JsonValue v && v.TryGetValue<double>(out var n) && double.IsFinite(n) ? Math.Clamp(100 - n, 0, 100) : null;
    public static string Reset(JsonNode? w)
    {
        if (w?["resetsAt"] is not JsonValue v || !v.TryGetValue<long>(out var seconds)) return "Reset time unavailable";
        try
        {
            var date = DateTimeOffset.FromUnixTimeSeconds(seconds).ToLocalTime();
            var left = date - DateTimeOffset.Now;
            if (left.TotalSeconds <= 0) return "Reset due · awaiting refresh";
            var time = left.TotalDays >= 1 ? $"{(int)left.TotalDays}d {left.Hours}h" : left.TotalHours >= 1 ? $"{(int)left.TotalHours}h {left.Minutes}m" : $"{Math.Max(1, (int)Math.Ceiling(left.TotalMinutes))}m";
            return $"Resets in {time} · {date:ddd h:mm tt}";
        }
        catch { return "Reset time unavailable"; }
    }
    public static IEnumerable<(string, JsonNode)> Buckets(JsonNode data)
    {
        if (data["rateLimitsByLimitId"] is JsonObject map && map.Any(p => p.Value != null))
        { foreach (var p in map) if (p.Value != null) yield return (p.Value["limitName"]?.ToString() ?? p.Key, p.Value); }
        else if (data["rateLimits"] is { } bucket) yield return (bucket["limitName"]?.ToString() ?? bucket["limitId"]?.ToString() ?? "Codex", bucket);
    }
}
