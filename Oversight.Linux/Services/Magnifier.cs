using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Avalonia.Threading;
using Oversight.Linux.Platform;
using Oversight.Models;
using Oversight.Services;
using Tmds.DBus.Protocol;

namespace Oversight.Linux.Services;

/// <summary>
/// Screen zoom through the desktop's own magnifier: GNOME (and Unity, Budgie) and Cinnamon through their gsettings
/// keys, KDE Plasma through KWin's zoom shortcuts. Same controls as the Windows magnifier (toggle or hold, zoom in/out,
/// default level); the user's own magnifier settings are put back when zoom turns off, on quit and after a crash.
/// </summary>
public sealed class Magnifier : IDisposable
{
    private enum Kind { None, GSettings, KWin }

    private static readonly Kind Desktop;
    private static readonly string AppsSchema = "", MagSchema = "";
    private static readonly string RestoreFile = Path.Combine(ConfigStore.ConfigDirectory, "magnifier-restore.json");

    static Magnifier()
    {
        string desktop = Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP") ?? "";
        bool Has(string name) => desktop.Split(':').Any(d => d.Contains(name, StringComparison.OrdinalIgnoreCase));
        if (Has("Cinnamon")) (Desktop, AppsSchema, MagSchema) = (Kind.GSettings, "org.cinnamon.desktop.a11y.applications", "org.cinnamon.desktop.a11y.magnifier");
        else if (Has("GNOME") || Has("Unity") || Has("Budgie")) (Desktop, AppsSchema, MagSchema) = (Kind.GSettings, "org.gnome.desktop.a11y.applications", "org.gnome.desktop.a11y.magnifier");
        else if (Has("KDE")) Desktop = Kind.KWin;
    }

    /// <summary>Whether this desktop has a magnifier Oversight can drive. Without one the zoom hotkeys aren't grabbed.</summary>
    public static bool Supported => Desktop != Kind.None;

    private readonly AppSettings _settings;
    private readonly DispatcherTimer _holdTimer;
    private static readonly SemaphoreSlim Queue = new(1, 1);
    private bool _holdMode;
    private uint _holdKey;
    private int _kwinSteps;   // KWin zoom-in steps currently applied

    public bool IsActive { get; private set; }
    public float ZoomLevel { get; private set; }
    public event Action? StateChanged;

    public Magnifier(AppSettings settings)
    {
        _settings = settings;
        ZoomLevel = settings.Zoom.DefaultZoomLevel;
        // Hold-to-zoom: the hotkey only reports the press, so poll the key for release.
        _holdTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(15) };
        _holdTimer.Tick += (_, _) =>
        {
            if (_holdMode && !KeyMap.IsDown(_holdKey)) { _holdMode = false; Deactivate(); }
        };
    }

    public void Toggle()
    {
        _holdMode = false;
        if (IsActive) Deactivate();
        else Activate();
    }

    public void BeginHold(uint virtualKey)
    {
        _holdKey = virtualKey;
        _holdMode = true;
        _holdTimer.Start();
        Activate();
    }

    public void Activate()
    {
        if (!Supported || IsActive) return;
        IsActive = true;
        ZoomLevel = _settings.Zoom.DefaultZoomLevel;
        float level = ZoomLevel;
        Run(async () =>
        {
            await SaveOriginalStateAsync();
            if (Desktop == Kind.GSettings)
            {
                await GSettings("set", MagSchema, "mag-factor", Num(level));
                await GSettings("set", MagSchema, "mouse-tracking", "proportional");
                await GSettings("set", AppsSchema, "screen-magnifier-enabled", "true");
            }
            else await KWinTo(KWinStepsFor(level));
        });
        StateChanged?.Invoke();
    }

    public void Deactivate(bool animate = true)
    {
        _holdMode = false;
        _holdTimer.Stop();
        if (!IsActive) return;
        IsActive = false;
        Run(async () =>
        {
            await RestoreAsync(_kwinSteps > 0);
            _kwinSteps = 0;
        });
        StateChanged?.Invoke();
    }

    public void ZoomIn() => Adjust(+1);
    public void ZoomOut() => Adjust(-1);

    private void Adjust(int direction)
    {
        if (!IsActive) return;
        if (Desktop == Kind.KWin)
        {
            int steps = Math.Max(1, KWinStepsFor(ZoomLevel) + direction);
            ZoomLevel = (float)Math.Pow(KWinFactor, steps);
            Run(() => KWinTo(steps));
        }
        else
        {
            ZoomLevel = Math.Clamp(ZoomLevel + direction * _settings.Zoom.ZoomStep, _settings.Zoom.MinZoom, _settings.Zoom.MaxZoom);
            float level = ZoomLevel;
            Run(() => GSettings("set", MagSchema, "mag-factor", Num(level)));
        }
        StateChanged?.Invoke();
    }

    // ── Desktop calls, one at a time in order ────────────────────────────────

    private static void Run(Func<Task> work) => _ = Task.Run(async () =>
    {
        await Queue.WaitAsync();
        try { await work(); }
        catch (Exception ex) { ErrorLog.Write(ex); }
        finally { Queue.Release(); }
    });

    private static string Num(float v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    private static async Task<string?> GSettings(params string[] args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("gsettings", args) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false });
            if (p is null) return null;
            string output = await p.StandardOutput.ReadToEndAsync();
            await p.WaitForExitAsync();
            return p.ExitCode == 0 ? output.Trim() : null;
        }
        catch (System.ComponentModel.Win32Exception) { return null; }
    }

    // KWin multiplies the zoom by its ZoomFactor (1.2 unless changed in kwinrc) on each step.
    private static readonly double KWinFactor = ReadKWinFactor();

    private static double ReadKWinFactor()
    {
        try
        {
            string rc = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "kwinrc");
            bool inZoom = false;
            foreach (string line in File.ReadLines(rc))
            {
                if (line.StartsWith('[')) inZoom = line.Trim() == "[Effect-zoom]";
                else if (inZoom && line.StartsWith("ZoomFactor=") && double.TryParse(line[11..], CultureInfo.InvariantCulture, out double f) && f > 1)
                    return f;
            }
        }
        catch (IOException) { }
        return 1.2;
    }

    private static int KWinStepsFor(float level) => Math.Max(1, (int)Math.Round(Math.Log(level) / Math.Log(KWinFactor)));

    private async Task KWinTo(int steps)
    {
        while (_kwinSteps < steps) { await KWin("view_zoom_in"); _kwinSteps++; }
        while (_kwinSteps > steps) { await KWin("view_zoom_out"); _kwinSteps--; }
    }

    private static async Task KWin(string shortcut)
    {
        try
        {
            using var connection = new Connection(Address.Session!);
            await connection.ConnectAsync();
            await connection.CallMethodAsync(Invoke(connection, shortcut));
        }
        catch (Exception ex) when (ex is DBusException or ConnectException or IOException or DisconnectedException) { }
    }

    private static MessageBuffer Invoke(Connection connection, string shortcut)
    {
        using var w = connection.GetMessageWriter();
        w.WriteMethodCallHeader("org.kde.kglobalaccel", "/component/kwin", "org.kde.kglobalaccel.Component", "invokeShortcut", "s");
        w.WriteString(shortcut);
        return w.CreateMessage();
    }

    // ── The user's own magnifier state ───────────────────────────────────────

    private sealed record Original(string? Enabled, string? Factor, string? Tracking);

    /// <summary>Remembers the magnifier settings before the first zoom, on disk so a crash can't lose them.</summary>
    private static async Task SaveOriginalStateAsync()
    {
        if (File.Exists(RestoreFile)) return;
        var original = Desktop == Kind.GSettings
            ? new Original(await GSettings("get", AppsSchema, "screen-magnifier-enabled"),
                           await GSettings("get", MagSchema, "mag-factor"), await GSettings("get", MagSchema, "mouse-tracking"))
            : new Original(null, null, null);
        Directory.CreateDirectory(ConfigStore.ConfigDirectory);
        await File.WriteAllTextAsync(RestoreFile, JsonSerializer.Serialize(original));
    }

    private static async Task RestoreAsync(bool kwinZoomed)
    {
        if (Desktop == Kind.KWin)
        {
            if (kwinZoomed || File.Exists(RestoreFile)) await KWin("view_actual_size");
        }
        else if (File.Exists(RestoreFile) && JsonSerializer.Deserialize<Original>(await File.ReadAllTextAsync(RestoreFile)) is { } o)
        {
            await GSettings("set", AppsSchema, "screen-magnifier-enabled", o.Enabled ?? "false");
            if (o.Factor is not null) await GSettings("set", MagSchema, "mag-factor", o.Factor);
            if (o.Tracking is not null) await GSettings("set", MagSchema, "mouse-tracking", o.Tracking.Trim('\''));
        }
        try { File.Delete(RestoreFile); } catch (IOException) { }
    }

    /// <summary>
    /// Waits for pending magnifier calls, then puts the user's magnifier back if Oversight is quitting or crashed while
    /// zoomed (also run at start, after a crash). Safe from exit and crash handlers.
    /// </summary>
    public static void EmergencyReset()
    {
        try
        {
            if (!Supported || !Queue.Wait(TimeSpan.FromSeconds(3))) return;
            try { if (File.Exists(RestoreFile)) RestoreAsync(kwinZoomed: true).Wait(TimeSpan.FromSeconds(3)); }
            finally { Queue.Release(); }
        }
        catch { /* nothing more we can do during teardown */ }
    }

    public void Dispose()
    {
        _holdTimer.Stop();
        IsActive = false;
        EmergencyReset();
    }
}
