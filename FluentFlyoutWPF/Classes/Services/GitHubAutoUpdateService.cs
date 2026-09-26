// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using FluentFlyout.Classes.Settings;
using NLog;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text;
using System.Threading;

namespace FluentFlyoutWPF.Classes.Services;

/// <summary>Installs published GitHub MSIX releases for non-Store installations only.</summary>
public sealed class GitHubAutoUpdateService : IDisposable
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    private static readonly Uri LatestRelease = new("https://api.github.com/repos/unchihugo/FluentFlyout/releases/latest");
    private readonly HttpClient _client = new() { Timeout = TimeSpan.FromMinutes(5) };
    private readonly CancellationTokenSource _stop = new();
    private bool _started;

    public GitHubAutoUpdateService()
    {
        _client.DefaultRequestHeaders.UserAgent.ParseAdd("FluentFlyout-Updater");
        _client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }

    public void Start()
    {
        if (_started) return;
        _started = true;
        _ = RunAsync(_stop.Token);
    }

    private async Task RunAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                var settings = SettingsManager.Current;
                if (!settings.IsStoreVersion && settings.AutoUpdateEnabled &&
                    settings.LastKnownVersion != "debug" &&
                    DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeSeconds(Math.Max(0, settings.LastAutomaticUpdateCheckUnixSeconds)) >= Interval(settings.AutoUpdateInterval))
                {
                    // Record the attempt before making a request so failures cannot cause a tight retry loop.
                    settings.LastAutomaticUpdateCheckUnixSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    await CheckAndInstallAsync(settings.LastKnownVersion, token);
                }
                await Task.Delay(TimeSpan.FromMinutes(1), token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                Logger.Warn(ex, "Automatic update check failed");
                try { await Task.Delay(TimeSpan.FromMinutes(1), token); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    internal static TimeSpan Interval(int selection) => selection switch
    {
        1 => TimeSpan.FromDays(7),
        2 => TimeSpan.FromDays(30),
        _ => TimeSpan.FromDays(1)
    };

    private async Task CheckAndInstallAsync(string installedVersion, CancellationToken token)
    {
        using var response = await _client.GetAsync(LatestRelease, token);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(token));
        var release = json.RootElement;
        string? tag = release.GetProperty("tag_name").GetString();
        if (!Version.TryParse(installedVersion.TrimStart('v', 'V'), out var current) ||
            !Version.TryParse(tag?.TrimStart('v', 'V'), out var latest) || latest <= current) return;

        // Never execute scripts from a release. Only download a packaged app from the official release.
        foreach (var asset in release.GetProperty("assets").EnumerateArray())
        {
            string? name = asset.GetProperty("name").GetString();
            string? url = asset.GetProperty("browser_download_url").GetString();
            if (name == null || !name.EndsWith(".msixbundle", StringComparison.OrdinalIgnoreCase) ||
                url == null || !Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
                uri.Scheme != Uri.UriSchemeHttps || uri.Host != "github.com" ||
                !uri.AbsolutePath.StartsWith("/unchihugo/FluentFlyout/releases/download/", StringComparison.OrdinalIgnoreCase)) continue;

            string file = Path.Combine(Path.GetTempPath(), $"FluentFlyout-{Guid.NewGuid():N}.msixbundle");
            try
            {
                using var download = await _client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token);
                download.EnsureSuccessStatusCode();
                await using (var stream = File.Create(file))
                    await download.Content.CopyToAsync(stream, token);

                // Add-AppxPackage enforces Windows package signature and publisher validation.
                // The installer is run outside the app process so a running package can be replaced.
                var start = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true };
                start.ArgumentList.Add("-NoProfile");
                start.ArgumentList.Add("-NonInteractive");
                // Encode the command rather than passing a path through PowerShell's argument parser.
                string quoted = file.Replace("'", "''");
                string script = $"$p='{quoted}'; try {{ Add-AppxPackage -Path $p -ErrorAction Stop }} finally {{ Remove-Item -LiteralPath $p -Force -ErrorAction SilentlyContinue }}";
                start.ArgumentList.Add("-EncodedCommand");
                start.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));
                using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start MSIX installer");
                Logger.Info($"Started MSIX update to {tag}");
                // Stop further checks until the new package starts. No forced application shutdown.
                _stop.Cancel();
                return;
            }
            catch
            {
                try { File.Delete(file); } catch (IOException) { /* best effort */ }
                throw;
            }
        }
        Logger.Info($"GitHub release {tag} contains no MSIX bundle; manual installation is required");
    }

    public void Dispose()
    {
        _stop.Cancel();
        // The in-flight request observes cancellation; dispose HTTP resources on process exit.
    }
}
