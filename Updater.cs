using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace CodexUsageWidget;

sealed record UpdateInfo(Version Version, string Tag, Uri DownloadUrl, string? Digest);

static class Updater
{
    const string LatestReleaseApi = "https://api.github.com/repos/mastachef/codex-usage-widget/releases/latest";
    const string LatestReleasePage = "https://github.com/mastachef/codex-usage-widget/releases/latest";
    static readonly HttpClient Client = CreateClient();

    public static Version CurrentVersion =>
        Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);

    static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("CodexUsageWidget/" + CurrentVersion);
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    public static async Task<UpdateInfo?> CheckAsync(CancellationToken cancellationToken = default)
    {
        using var response = await Client.GetAsync(LatestReleaseApi, cancellationToken);
        response.EnsureSuccessStatusCode();
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var tag = json?["tag_name"]?.ToString();
        if (string.IsNullOrWhiteSpace(tag)) return null;

        var versionText = tag.Trim().TrimStart('v', 'V');
        if (!Version.TryParse(versionText, out var latest) || latest <= CurrentVersion) return null;

        var asset = json?["assets"]?.AsArray()
            .FirstOrDefault(a => string.Equals(a?["name"]?.ToString(), "CodexUsageWidget.exe", StringComparison.OrdinalIgnoreCase));
        var urlText = asset?["browser_download_url"]?.ToString();
        if (!Uri.TryCreate(urlText, UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps) return null;

        return new UpdateInfo(latest, tag, url, asset?["digest"]?.ToString());
    }

    public static async Task DownloadAndInstallAsync(UpdateInfo update, IProgress<int>? progress = null, CancellationToken cancellationToken = default)
    {
        EnsureInstallDirectoryWritable();

        var updateDir = Path.Combine(Widget.DataRoot, "updates");
        Directory.CreateDirectory(updateDir);
        var staged = Path.Combine(updateDir, $"CodexUsageWidget-{update.Tag}.exe.download");

        using (var response = await Client.GetAsync(update.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
        {
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength;
            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var output = new FileStream(staged, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 128, true);
            var buffer = new byte[1024 * 128];
            long read = 0;
            while (true)
            {
                var count = await input.ReadAsync(buffer, cancellationToken);
                if (count == 0) break;
                await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
                read += count;
                if (total is > 0) progress?.Report((int)Math.Clamp(read * 100 / total.Value, 0, 100));
            }
        }

        VerifyDigest(staged, update.Digest);

        var destination = Application.ExecutablePath;
        var script = Path.Combine(updateDir, "apply-update.cmd");
        static string Batch(string value) => value.Replace("%", "%%");
        var src = Batch(staged);
        var dst = Batch(destination);

        File.WriteAllText(script, $"""
@echo off
setlocal
set "src={src}"
set "dst={dst}"

for /L %%i in (1,1,60) do (
  >nul 2>&1 copy /Y "%src%" "%dst%"
  if not errorlevel 1 goto launch
  timeout /t 1 /nobreak >nul
)

start "" "{LatestReleasePage}"
exit /b 1

:launch
del /Q "%src%" >nul 2>&1
start "" "%dst%"
(goto) 2>nul & del "%~f0"
""");

        Process.Start(new ProcessStartInfo("cmd.exe", $"/d /c \"\"{script}\"\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = updateDir
        });
    }

    static void VerifyDigest(string path, string? digest)
    {
        if (string.IsNullOrWhiteSpace(digest) || !digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)) return;
        var expected = digest["sha256:".Length..].Trim();
        using var stream = File.OpenRead(path);
        var actual = Convert.ToHexString(SHA256.HashData(stream));
        if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
        {
            try { File.Delete(path); } catch { }
            throw new InvalidDataException("The downloaded update failed SHA-256 verification.");
        }
    }

    static void EnsureInstallDirectoryWritable()
    {
        var directory = Path.GetDirectoryName(Application.ExecutablePath) ?? AppContext.BaseDirectory;
        var probe = Path.Combine(directory, ".codexusage-update-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
        }
        catch (Exception ex)
        {
            throw new UnauthorizedAccessException(
                "This copy is in a folder Windows will not let the widget update. Move CodexUsageWidget.exe to a writable folder such as your user Apps folder, then try again.",
                ex);
        }
    }
}
