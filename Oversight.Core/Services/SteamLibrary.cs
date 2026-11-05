using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace Oversight.Services;

/// <summary>An installed Steam game. <see cref="Exes"/> are the likely game executables, best guess first.</summary>
public sealed record SteamGame(string AppId, string Name, string InstallDir, IReadOnlyList<string> Exes, string? IconPath)
{
    public override string ToString() => Name;
}

/// <summary>
/// Lists installed Steam games from the library manifests (libraryfolders.vdf + appmanifest_*.acf), without launching
/// anything, and guesses each game's executable so a Game Profile can match its process.
/// </summary>
public static partial class SteamLibrary
{
    /// <summary>Steam's install folder, or null when Steam isn't installed.</summary>
    public static string? FindRoot()
    {
        if (OperatingSystem.IsWindows())
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            return key?.GetValue("SteamPath") is string p && Directory.Exists(p) ? Path.GetFullPath(p) : null;
        }
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return new[] { ".steam/steam", ".local/share/Steam", ".var/app/com.valvesoftware.Steam/.local/share/Steam" }
            .Select(p => Path.Combine(home, p))
            .FirstOrDefault(p => Directory.Exists(Path.Combine(p, "steamapps")));
    }

    /// <summary>Every installed game in every library, sorted by name. Slow-ish (walks install folders): call off the UI thread.</summary>
    public static List<SteamGame> Scan(string root)
    {
        var libraries = new List<string> { root };
        try
        {
            foreach (Match m in VdfPath().Matches(File.ReadAllText(Path.Combine(root, "steamapps", "libraryfolders.vdf"))))
                libraries.Add(Unescape(m.Groups[1].Value));
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        var games = new List<SteamGame>();
        var seen = new HashSet<string>();
        foreach (string lib in libraries.Select(l => Path.GetFullPath(l).TrimEnd('/', '\\')).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string apps = Path.Combine(lib, "steamapps");
            if (!Directory.Exists(apps)) continue;
            foreach (string acf in Directory.EnumerateFiles(apps, "appmanifest_*.acf"))
            {
                try
                {
                    string text = File.ReadAllText(acf);
                    string? id = Field(text, "appid"), name = Field(text, "name"), dir = Field(text, "installdir");
                    if (id is null || name is null || dir is null || !seen.Add(id) || IsTool(name)) continue;
                    string installDir = Path.Combine(apps, "common", dir);
                    var exes = FindExes(installDir);
                    if (exes.Count > 0) games.Add(new SteamGame(id, name, installDir, exes, IconFor(root, id)));
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        games.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return games;
    }

    /// <summary>Steam's cached library icon for an app, or null.</summary>
    public static string? IconFor(string root, string appId)
    {
        string cache = Path.Combine(root, "appcache", "librarycache");
        string old = Path.Combine(cache, appId + "_icon.jpg");
        if (File.Exists(old)) return old;
        string dir = Path.Combine(cache, appId);
        if (!Directory.Exists(dir)) return null;
        // Newer clients keep the icon as <sha1 of the icon>.jpg beside header.jpg etc.
        return Directory.EnumerateFiles(dir, "*.jpg").FirstOrDefault(f => Sha1Name().IsMatch(Path.GetFileName(f)));
    }

    /// <summary>The name a profile matches for this executable: the Windows process name, or on Linux what /proc/pid/comm shows.</summary>
    public static string ProcessNameFor(string exePath)
    {
        string file = Path.GetFileName(exePath);
        if (!OperatingSystem.IsLinux()) return Path.GetFileNameWithoutExtension(file);
        // comm is cut to 15 bytes (Proton games show as e.g. "BeamNG.drive.x6"); X11.ProcessName strips a whole ".exe".
        if (file.Length > 15) file = file[..15];
        return file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? file[..^4] : file;
    }

    // Proton, the Steam Linux Runtime and Steamworks redistributables are installed like games.
    private static bool IsTool(string name) =>
        name.StartsWith("Proton", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("Steam Linux Runtime", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("Steamworks", StringComparison.OrdinalIgnoreCase);

    private static readonly string[] JunkNames =
    [
        "crash", "redist", "dxsetup", "dxwebsetup", "dotnetfx", "unins", "setup", "install", "prereq",
        "easyanticheat", "start_protected_game", "battleye", "beservice", "updater", "helper", "cefprocess", "cefsubprocess",
    ];

    private static readonly string[] JunkDirs =
        ["_commonredist", "redist", "redistributables", "easyanticheat", "battleye", "directx", "vcredist", "dotnet", "__installer", "installer"];

    /// <summary>Likely game executables under an install folder (three folders deep), best guess first.</summary>
    public static List<string> FindExes(string installDir)
    {
        if (!Directory.Exists(installDir)) return [];
        bool linux = OperatingSystem.IsLinux();
        bool arm = RuntimeInformation.OSArchitecture == Architecture.Arm64;
        var opts = new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 3, IgnoreInaccessible = true };

        var found = new List<FileInfo>();
        foreach (var f in new DirectoryInfo(installDir).EnumerateFiles(linux ? "*" : "*.exe", opts))
        {
            string name = f.Name.ToLowerInvariant();
            string rel = Path.GetRelativePath(installDir, f.DirectoryName!).ToLowerInvariant();
            var dirs = rel.Split('/', '\\');
            if (JunkNames.Any(name.Contains) || dirs.Any(d => JunkDirs.Contains(d) || d.EndsWith("_data"))) continue;
            if (!arm && (name.Contains("arm64") || rel.Contains("arm64"))) continue;
            if (linux && !name.EndsWith(".exe") && !IsLinuxExecutable(f)) continue;
            found.Add(f);
        }

        // Unity: the game is the exe with a "<name>_Data" folder beside it (Rust.exe is only the anti-cheat launcher).
        var unity = found.Where(f => Directory.Exists(Path.Combine(f.DirectoryName!, Path.GetFileNameWithoutExtension(f.Name) + "_Data"))).ToList();
        // Unreal: the game is "<Game>-Win64-Shipping.exe"; the root "<Game>.exe" only starts it.
        var shipping = found.Where(f => Path.GetFileNameWithoutExtension(f.Name).EndsWith("-Shipping", StringComparison.OrdinalIgnoreCase)).ToList();
        if (unity.Count > 0) found = unity;
        else if (shipping.Count > 0) found = shipping;

        return found.OrderByDescending(f => f.Length).Select(f => f.FullName).ToList();
    }

    private static bool IsLinuxExecutable(FileInfo f)
    {
        if (f.Name.Contains(".so") || (f.UnixFileMode & UnixFileMode.UserExecute) == 0) return false;
        try
        {
            using var s = f.OpenRead();
            Span<byte> magic = stackalloc byte[4];
            return s.Read(magic) == 4 && magic is [0x7F, (byte)'E', (byte)'L', (byte)'F'];
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private static string? Field(string text, string key) =>
        Regex.Match(text, $"\"{key}\"\\s+\"((?:\\\\.|[^\"\\\\])*)\"", RegexOptions.IgnoreCase) is { Success: true } m ? Unescape(m.Groups[1].Value) : null;

    private static string Unescape(string s) => s.Replace("\\\\", "\\").Replace("\\\"", "\"");

    [GeneratedRegex("\"path\"\\s+\"((?:\\\\.|[^\"\\\\])*)\"", RegexOptions.IgnoreCase)]
    private static partial Regex VdfPath();

    [GeneratedRegex("^[0-9a-f]{40}\\.jpg$", RegexOptions.IgnoreCase)]
    private static partial Regex Sha1Name();
}
