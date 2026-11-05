using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace Oversight.Services;

public sealed record UpdateInfo(Version Version, string Tag, string PageUrl, string ZipUrl, string? SumsUrl);

/// <summary>
/// Updates from the project's GitHub Releases (same scheme as Roamwise). Downloads only from github.com, checks the
/// SHA-256 published with the release, swaps Oversight.exe in place (a running exe can be renamed) and restarts.
/// </summary>
public sealed class Updater
{
    public const string Repo = "TzTech26/OverSight";
    public static readonly string ReleasesPage = $"https://github.com/{Repo}/releases";
    private const string AssetName = "Oversight-win-x64.zip";

    private readonly ConfigStore _config;

    public static Version CurrentVersion { get; } =
        Normalize(Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(1, 0, 0));

    public UpdateInfo? Available { get; private set; }

    /// <summary>Raised after the new copy has started; the app should exit.</summary>
    public event Action? QuitRequested;

    public Updater(ConfigStore config) => _config = config;

    /// <summary>Only the published single-file exe can replace itself; a dev build (Oversight.dll beside it) can't.</summary>
    public static bool CanSelfUpdate => Environment.ProcessPath is string exe && !AppProfile.IsIsolated
        && !File.Exists(Path.Combine(Path.GetDirectoryName(exe)!, "Oversight.dll"));

    /// <summary>Appends a line to update.log in the config folder (kept small). Never throws.</summary>
    public static void Log(string line)
    {
        try
        {
            string path = Path.Combine(ConfigStore.ConfigDirectory, "update.log");
            Directory.CreateDirectory(ConfigStore.ConfigDirectory);
            if (File.Exists(path) && new FileInfo(path).Length > 64 * 1024) File.Delete(path);
            File.AppendAllText(path, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {line}{Environment.NewLine}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Removes the previous copy left behind by an update.</summary>
    public static void CleanUpAfterUpdate()
    {
        if (Environment.ProcessPath is not string exe) return;
        foreach (string old in Directory.EnumerateFiles(Path.GetDirectoryName(exe)!, Path.GetFileName(exe) + ".old*"))
            try { File.Delete(old); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    public async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
    {
        using var http = Client();
        var release = await http.GetFromJsonAsync<JsonElement>($"https://api.github.com/repos/{Repo}/releases/latest", ct);
        _config.Updates.LastCheck = DateTime.Now;
        _config.Save();

        string tag = release.GetProperty("tag_name").GetString() ?? "";
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var version)) return null;
        version = Normalize(version);

        string? zip = null, sums = null;
        foreach (var asset in release.GetProperty("assets").EnumerateArray())
        {
            string name = asset.GetProperty("name").GetString() ?? "";
            string url = asset.GetProperty("browser_download_url").GetString() ?? "";
            if (name == AssetName) zip = url;
            if (name == "SHA256SUMS.txt") sums = url;
        }

        Available = version > CurrentVersion && zip is not null
            ? new UpdateInfo(version, tag, release.GetProperty("html_url").GetString() ?? ReleasesPage, zip, sums)
            : null;
        return Available;
    }

    /// <summary>Background check, at most every 20 hours. Installs on its own when that's turned on, otherwise calls notify.</summary>
    public async Task CheckInBackgroundAsync(Action<UpdateInfo> notify)
    {
        var s = _config.Updates;
        if (!s.CheckForUpdates) return;
        if (s.LastCheck is DateTime last && DateTime.Now - last < TimeSpan.FromHours(20)) return;
        try
        {
            if (await CheckAsync() is not { } update) return;
            if (s.InstallAutomatically && CanSelfUpdate && await InstallAsync(update) is null) return;
            notify(update);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException)
        {
            // Offline or rate-limited; try again next time.
        }
    }

    /// <summary>Downloads, verifies and swaps in the new exe, then starts it. Returns an error sentence on failure.</summary>
    public async Task<string?> InstallAsync(UpdateInfo update, CancellationToken ct = default)
    {
        if (!CanSelfUpdate || Environment.ProcessPath is not string target)
            return "This copy of Oversight was built from source, so it can't update itself. Download the release instead.";
        if (!IsGitHub(update.ZipUrl) || update.SumsUrl is null || !IsGitHub(update.SumsUrl))
            return "This release has no checksum file on GitHub, so Oversight won't install it automatically.";

        string work = Path.Combine(Path.GetTempPath(), $"Oversight-update-{update.Version}");
        try
        {
            Directory.CreateDirectory(work);
            string download = Path.Combine(work, AssetName);
            using (var http = Client())
            {
                await File.WriteAllBytesAsync(download, await http.GetByteArrayAsync(update.ZipUrl, ct), ct);
                string sums = await http.GetStringAsync(update.SumsUrl, ct);
                string? expected = sums.Split('\n')
                    .Select(l => l.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    .FirstOrDefault(p => p.Length == 2 && p[1].TrimStart('*') == AssetName)?[0];
                string actual = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(download, ct)));
                if (expected is null || !actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
                    return "The download didn't match the published checksum, so it wasn't installed.";
            }

            string extracted = Path.Combine(work, "out");
            if (Directory.Exists(extracted)) Directory.Delete(extracted, true);
            ZipFile.ExtractToDirectory(download, extracted);
            string newCopy = Path.Combine(extracted, "Oversight.exe");
            if (!File.Exists(newCopy)) return "The download didn't contain Oversight.exe.";

            // The running exe can be renamed but not overwritten. A previous copy that's still running can't be
            // deleted either, so it moves aside and goes at the next start.
            try { File.Delete(target + ".old"); }
            catch (UnauthorizedAccessException) { File.Move(target + ".old", $"{target}.old-{Guid.NewGuid():N}"); }
            File.Move(target, target + ".old");
            try { File.Copy(newCopy, target); }
            catch { File.Move(target + ".old", target, overwrite: true); throw; }

            // The new copy waits for this one to exit before taking the single-instance lock and the hotkeys.
            try { Process.Start(new ProcessStartInfo(target, $"--updated --wait {Environment.ProcessId}") { UseShellExecute = false }); }
            catch (System.ComponentModel.Win32Exception)
            {
                // Windows refused to run the new copy (Smart App Control, antivirus): put this one back.
                File.Delete(target);
                File.Move(target + ".old", target);
                return $"Windows blocked the new version from starting, so Oversight kept {CurrentVersion}.";
            }
            Log($"{CurrentVersion}: started {update.Version}, quitting.");
            QuitRequested?.Invoke();
            // Fallback if the normal shutdown stalls: two copies would fight over the hotkeys.
            _ = Task.Delay(TimeSpan.FromSeconds(10)).ContinueWith(_ =>
            {
                Log($"{CurrentVersion}: still running 10 s after the hand-off, forcing exit.");
                Environment.Exit(0);
            });
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return $"Oversight can't write to {Path.GetDirectoryName(target)}. Move it to a folder you own or download the update yourself.";
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or InvalidDataException or TaskCanceledException)
        {
            return $"The update failed: {ex.Message}";
        }
        finally
        {
            try { Directory.Delete(work, true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private static bool IsGitHub(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == "https"
        && (u.Host == "github.com" || u.Host.EndsWith(".githubusercontent.com"));

    private static HttpClient Client()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"Oversight/{CurrentVersion}");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return http;
    }

    private static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0));
}
