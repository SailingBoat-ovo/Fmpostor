using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Fmpostor.Server.WebAdmin;

/// <summary>
///     Self-update service (Turbo-620): checks the panel server version against the
///     latest GitHub release of SailingBoat-ovo/Fmpostor and, on request, downloads the
///     matching OS asset, stages it under update_tmp\, writes a small takeover script
///     and exits so the script can swap the binaries and restart the server.
///     config.json / webadmin_*.json are never shipped inside release assets, so a
///     plain copy of the extracted payload cannot clobber local configuration.
/// </summary>
public class UpdateService
{
    private const string Repo = "SailingBoat-ovo/Fmpostor";
    private const string CurrentVersion = "Turbo-630.0-20260829";

    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<UpdateService> _logger;
    private static int _applying;

    public UpdateService(IHttpClientFactory httpFactory, ILogger<UpdateService> logger)
    {
        _httpFactory = httpFactory;
        _logger = logger;
    }

    private static string CurrentTag
    {
        get
        {
            // "Turbo-630.0-20260829" -> "Turbo-630.0" (strip trailing -YYYYMMDD build stamp)
            var m = Regex.Match(CurrentVersion, @"^(.+?)-\d{8}$");
            return m.Success ? m.Groups[1].Value : CurrentVersion;
        }
    }

    /// <summary>True only when the latest tag is strictly NEWER than the running tag
    /// ("Turbo-630.0" &gt; "Turbo-620.0"). Prevents an old release from luring a newer
    /// server into a downgrade just because the tags differ.</summary>
    private static bool IsNewerTag(string latest, string current)
    {
        if (string.Equals(latest, current, StringComparison.OrdinalIgnoreCase)) return false;
        var m1 = Regex.Match(latest ?? "", @"^Turbo-(\d+)(?:\.(\d+))?$", RegexOptions.IgnoreCase);
        var m2 = Regex.Match(current ?? "", @"^Turbo-(\d+)(?:\.(\d+))?$", RegexOptions.IgnoreCase);
        if (!m1.Success || !m2.Success)
            return true; // non-numeric tags: fall back to "tag differs"
        long a = long.Parse(m1.Groups[1].Value) * 1000 + (m1.Groups[2].Success ? long.Parse(m1.Groups[2].Value) : 0);
        long b = long.Parse(m2.Groups[1].Value) * 1000 + (m2.Groups[2].Success ? long.Parse(m2.Groups[2].Value) : 0);
        return a > b;
    }

    public async Task<object> CheckAsync()
    {
        var client = _httpFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(20);
        using var req = new HttpRequestMessage(HttpMethod.Get,
            "https://api.github.com/repos/" + Repo + "/releases/latest");
        req.Headers.UserAgent.ParseAdd("Fmpostor-Update");
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        using var resp = await client.SendAsync(req);
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadAsByteArrayAsync();
        using var doc = await System.Text.Json.JsonDocument.ParseAsync(new MemoryStream(json));
        var root = doc.RootElement;
        var latestTag = (root.TryGetProperty("tag_name", out var tn) ? tn.GetString() : null) ?? "";
        if (latestTag.StartsWith("v", StringComparison.OrdinalIgnoreCase)) latestTag = latestTag[1..];
        var latestName = (root.TryGetProperty("name", out var nm) ? nm.GetString() : null) ?? latestTag;
        var notes = (root.TryGetProperty("body", out var b) ? b.GetString() : null) ?? "";
        var published = root.TryGetProperty("published_at", out var pa) ? pa.GetString() : null;

        // Pick the asset matching the current OS.
        var wantWin = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        string? assetUrl = null, assetName = null;
        long assetSize = 0;
        if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            foreach (var a in assets.EnumerateArray())
            {
                var n = (a.TryGetProperty("name", out var an) ? an.GetString() : null) ?? "";
                var match = wantWin
                    ? n.EndsWith("win-x64.zip", StringComparison.OrdinalIgnoreCase)
                    : n.EndsWith("linux-x64.tar.gz", StringComparison.OrdinalIgnoreCase);
                if (!match) continue;
                assetName = n;
                assetUrl = (a.TryGetProperty("browser_download_url", out var bu) ? bu.GetString() : null);
                assetSize = a.TryGetProperty("size", out var sz) ? sz.GetInt64() : 0;
                break;
            }
        }

        var updateAvailable = IsNewerTag(latestTag, CurrentTag);
        _logger.LogInformation("[Update] current={Cur} latest={Lat} available={Av} asset={Asset}",
            CurrentTag, latestTag, updateAvailable, assetName ?? "-");
        return new
        {
            success = true,
            currentVersion = CurrentVersion,
            currentTag = CurrentTag,
            latestVersion = latestName,
            latestTag,
            updateAvailable,
            releaseNotes = notes,
            publishedAt = published,
            asset = assetName,
            assetUrl,
            assetSize
        };
    }

    /// <summary>Downloads the matching release asset, stages it and arms the takeover script.</summary>
    public async Task<(bool ok, string message)> ApplyAsync()
    {
        if (Interlocked.Exchange(ref _applying, 1) != 0)
            return (false, "Update already in progress.");
        try
        {
            var check = await CheckAsync();
            var avail = (bool)(check.GetType().GetProperty("updateAvailable")?.GetValue(check) ?? false);
            if (!avail)
                return (false, "Already on the latest version (tag " + CurrentTag + ").");
            var assetUrl = (string?)check.GetType().GetProperty("assetUrl")?.GetValue(check);
            var assetName = (string?)check.GetType().GetProperty("asset")?.GetValue(check) ?? "asset";
            if (string.IsNullOrEmpty(assetUrl))
                return (false, "No matching asset found in the latest release for this OS.");

            var dir = AppContext.BaseDirectory;
            var tmp = Path.Combine(dir, "update_tmp");
            var staging = Path.Combine(tmp, "new");
            if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
            Directory.CreateDirectory(staging);

            _logger.LogInformation("[Update] downloading {Asset} ...", assetName);
            var client = _httpFactory.CreateClient();
            client.Timeout = TimeSpan.FromMinutes(5);
            using var dl = new HttpRequestMessage(HttpMethod.Get, assetUrl);
            dl.Headers.UserAgent.ParseAdd("Fmpostor-Update");
            using var dresp = await client.SendAsync(dl);
            dresp.EnsureSuccessStatusCode();
            var payload = await dresp.Content.ReadAsByteArrayAsync();
            var pkgPath = Path.Combine(tmp, assetName);
            await File.WriteAllBytesAsync(pkgPath, payload);
            _logger.LogInformation("[Update] downloaded {Bytes} bytes, extracting ...", payload.LongLength);

            // Extract to update_tmp\new (zip on Windows; the OS tar binary on Linux).
            if (assetName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                ZipFile.ExtractToDirectory(pkgPath, staging, overwriteFiles: true);
            }
            else
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "/bin/sh",
                    Arguments = "-c \"tar -xzf '" + pkgPath + "' -C '" + staging + "'\"",
                    UseShellExecute = false,
                    RedirectStandardError = true
                };
                using var tp = Process.Start(psi);
                if (tp == null) return (false, "Failed to start tar.");
                var terr = await tp.StandardError.ReadToEndAsync();
                await tp.WaitForExitAsync();
                if (tp.ExitCode != 0)
                    return (false, "tar extraction failed: " + terr);
            }

            // Sanity check: the new build must contain the server binary.
            var newExe = Path.Combine(staging, RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? "Fmpostor.Server.exe" : "Fmpostor.Server");
            if (!File.Exists(newExe))
                return (false, "Extracted payload does not contain the server binary.");

            WriteTakeoverScript(dir, tmp);

            _logger.LogInformation("[Update] takeover script armed; exiting so it can swap binaries.");
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(1500);
                    Environment.Exit(0);
                }
                catch { /* exiting anyway */ }
            });
            return (true, "Update staged; server restarting with the new version.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Update] apply failed");
            Interlocked.Exchange(ref _applying, 0);
            return (false, "Update failed: " + ex.Message);
        }
    }

    private static void WriteTakeoverScript(string dir, string tmp)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // ASCII-only batch file. Swaps binaries, restarts the server, deletes itself.
            var bat = Path.Combine(dir, "update.bat");
            var content = string.Join("\r\n",
                "@echo off",
                "cd /d \"%~dp0\"",
                "timeout /t 2 /nobreak >nul",
                "xcopy /y /e \"update_tmp\\new\\*.*\" \".\" >nul",
                "rmdir /s /q \"update_tmp\"",
                "start \"\" \"Fmpostor.Server.exe\"",
                "(goto) 2>nul & del \"%~f0\"");
            File.WriteAllText(bat, content);
            Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = "/c \"" + bat + "\"",
                WorkingDirectory = dir,
                CreateNoWindow = true,
                UseShellExecute = false
            });
        }
        else
        {
            var sh = Path.Combine(dir, "update.sh");
            var lines = new[]
            {
                "#!/bin/sh",
                "cd \"$(dirname \"$0\")\"",
                "sleep 2",
                "cp -f update_tmp/new/* .",
                "chmod +x Fmpostor.Server 2>/dev/null || true",
                "chmod +x Impostor.Server 2>/dev/null || true",
                "rm -rf update_tmp",
                // Under systemd the unit's Restart=always brings the server back;
                // otherwise re-launch it detached.
                "if [ ! -d /run/systemd/system ]; then nohup ./Fmpostor.Server >/dev/null 2>&1 & fi",
                "rm -f update.sh"
            };
            File.WriteAllText(sh, string.Join("\n", lines) + "\n");
            try { File.SetUnixFileMode(sh, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); } catch { }
            Process.Start(new ProcessStartInfo
            {
                FileName = "/bin/sh",
                Arguments = "\"" + sh + "\"",
                WorkingDirectory = dir,
                UseShellExecute = false
            });
        }
    }
}
