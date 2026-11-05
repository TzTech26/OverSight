using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Oversight.Models;

namespace Oversight.Services;

/// <summary>
/// Loads / saves settings and manages the two independent save-slot lists — zoom slots and
/// crosshair slots — plus the Game Profiles rules that pick one of each per app.
///
/// <para><b>Live instance invariant.</b> The services capture <see cref="Settings"/> (and its
/// members) by reference once at startup, so they must remain the *same objects* for the app's
/// lifetime. Switching a slot therefore copies the target slot's values <i>into</i> the live
/// instance rather than swapping references. The slot lists keep independent objects per
/// profile; the active slots are re-synced from the live instance on every <see cref="Save"/>.</para>
///
/// <para><b>Migration.</b> The config lives in %APPDATA%\Oversight. On first run the store
/// looks for the old %APPDATA%\ScreenZoom\settings.json (schema v2/v3, or the original bare
/// settings blob) and converts it: old profiles become zoom slots, the built-in crosshair
/// presets are seeded as crosshair slots, and the old auto-switch rules carry over.</para>
/// </summary>
public sealed class ConfigStore
{
    private static readonly string ConfigDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Oversight" + AppProfile.Suffix);

    private static readonly string ConfigPath = Path.Combine(ConfigDir, "settings.json");

    /// <summary>The folder holding settings.json (and error.log).</summary>
    public static string ConfigDirectory => ConfigDir;

    private static readonly string LegacyConfigPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ScreenZoom", "settings.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented          = true,
        Converters             = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    private const string DefaultZoomName = "Default";

    private List<ZoomProfile>       _zoomProfiles = [];
    private List<CrosshairProfile>  _xhProfiles   = [];
    private List<AutoClickerProfile> _acProfiles  = [];
    private List<AppRule>           _appRules     = [];
    private HotkeyBinding           _openSettingsHotkey = DefaultOpenSettingsHotkey();

    /// <summary>The live settings, shared by reference with the services.</summary>
    public AppSettings Settings { get; } = new();

    public string ActiveZoomProfileName        { get; private set; } = DefaultZoomName;
    public string ActiveCrosshairProfileName   { get; private set; } = string.Empty;
    public string ActiveAutoClickerProfileName { get; private set; } = string.Empty;

    public IReadOnlyList<string> ZoomProfileNames        => _zoomProfiles.Select(p => p.Name).ToList();
    public IReadOnlyList<string> CrosshairProfileNames   => _xhProfiles.Select(p => p.Name).ToList();
    public IReadOnlyList<string> AutoClickerProfileNames => _acProfiles.Select(p => p.Name).ToList();

    /// <summary>Global per-app rules (Game Profiles tab).</summary>
    public IReadOnlyList<AppRule> AppRules => _appRules;

    /// <summary>Zoom slot used when no listed app is focused; null = zoom off there.</summary>
    public string? DefaultZoomProfile { get; private set; }

    /// <summary>Crosshair slot used when no listed app is focused; null = no crosshair there.</summary>
    public string? DefaultCrosshairProfile { get; private set; }

    /// <summary>Auto-clicker slot used when no listed app is focused; null = auto-clicker off there (the default).</summary>
    public string? DefaultAutoClickerProfile { get; private set; }

    public HotkeyBinding OpenSettingsHotkey => _openSettingsHotkey;

    /// <summary>Update preferences; edit in place, then <see cref="Save"/>.</summary>
    public UpdateSettings Updates { get; private set; } = new();

    /// <summary>Raised when slots are added/removed/renamed or an active slot changes.</summary>
    public event Action? ProfilesChanged;

    private ZoomProfile ActiveZoom =>
        _zoomProfiles.FirstOrDefault(p => p.Name == ActiveZoomProfileName) ?? _zoomProfiles[0];

    private CrosshairProfile ActiveCrosshair =>
        _xhProfiles.FirstOrDefault(p => p.Name == ActiveCrosshairProfileName) ?? _xhProfiles[0];

    private AutoClickerProfile ActiveAutoClicker =>
        _acProfiles.FirstOrDefault(p => p.Name == ActiveAutoClickerProfileName) ?? _acProfiles[0];

    private static HotkeyBinding DefaultOpenSettingsHotkey() => new()
    {
        Modifiers  = Native.NativeMethods.MOD_CONTROL | Native.NativeMethods.MOD_ALT,
        VirtualKey = (uint)System.Windows.Input.KeyInterop.VirtualKeyFromKey(System.Windows.Input.Key.Z),
    };

    // ── Load ───────────────────────────────────────────────────────────────────

    public void Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                LoadFrom(File.ReadAllText(ConfigPath));
            }
            else if (File.Exists(LegacyConfigPath))
            {
                // First run after the Oversight rebrand: adopt the old ScreenZoom config.
                LoadFrom(File.ReadAllText(LegacyConfigPath));
                Save();   // persist the migrated config in the new location right away
            }
            else
            {
                InitDefault();
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ConfigStore] Load failed: {ex.Message}");
            InitDefault();
        }

        ComposeLive();
    }

    private void LoadFrom(string json)
    {
        // Peek at the shape: v4 has "ZoomProfiles"; v2/v3 have "Profiles"; the original
        // scheme was a bare settings blob with a "Hotkeys" object at the root.
        bool isV4, isProfileAware;
        using (var doc = JsonDocument.Parse(json))
        {
            var root = doc.RootElement;
            isV4           = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("ZoomProfiles", out _);
            isProfileAware = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("Profiles", out _);
        }

        if (isV4)
        {
            // v4 and v5 share this shape; a v4 file simply has no auto-clicker fields, which
            // EnsureValid backfills with the default slot below.
            var file = JsonSerializer.Deserialize<ConfigFile>(json, JsonOpts) ?? new ConfigFile();
            _zoomProfiles             = file.ZoomProfiles;
            _xhProfiles               = file.CrosshairProfiles;
            _acProfiles               = file.AutoClickerProfiles ?? [];
            _appRules                 = file.AppRules ?? [];
            DefaultZoomProfile        = NullIfEmpty(file.DefaultZoomProfile);
            DefaultCrosshairProfile   = NullIfEmpty(file.DefaultCrosshairProfile);
            DefaultAutoClickerProfile = NullIfEmpty(file.DefaultAutoClickerProfile);
            ActiveZoomProfileName        = file.ActiveZoomProfile;
            ActiveCrosshairProfileName   = file.ActiveCrosshairProfile;
            ActiveAutoClickerProfileName = file.ActiveAutoClickerProfile;
            _openSettingsHotkey       = file.OpenSettingsHotkey ?? DefaultOpenSettingsHotkey();
            Updates                   = file.Updates ?? new();
        }
        else
        {
            var legacy = isProfileAware
                ? JsonSerializer.Deserialize<LegacyConfigFile>(json, JsonOpts) ?? new LegacyConfigFile()
                : WrapBareLegacy(json);
            MigrateLegacy(legacy);
        }

        EnsureValid();
    }

    /// <summary>Wraps the original bare settings blob into a single-profile legacy file.</summary>
    private static LegacyConfigFile WrapBareLegacy(string json)
    {
        var settings = JsonSerializer.Deserialize<LegacyAppSettings>(json, JsonOpts) ?? new LegacyAppSettings();
        return new LegacyConfigFile
        {
            ActiveProfile  = DefaultZoomName,
            Profiles       = [new LegacyProfile { Name = DefaultZoomName, Settings = settings }],
            AppAssignments = null,
            DefaultProfile = null,
        };
    }

    /// <summary>Converts a v2/v3 config into the split zoom/crosshair slot schema.</summary>
    private void MigrateLegacy(LegacyConfigFile old)
    {
        if (old.Profiles.Count == 0)
            old.Profiles.Add(new LegacyProfile());

        // Old profiles → zoom slots (same names).
        _zoomProfiles = old.Profiles
            .Select(p => new ZoomProfile { Name = p.Name, Settings = p.Settings.ToZoomSettings() })
            .ToList();

        // Crosshair slots: the built-in presets, plus a slot per old profile that actually
        // had its crosshair turned on (named after the profile so its rules can follow it).
        _xhProfiles = CrosshairPresets.DefaultProfiles();
        var xhNames = _xhProfiles.Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var profileXhSlot = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in old.Profiles)
        {
            if (p.Settings.Crosshair is not { Enabled: true } xh) continue;

            var slot = new CrosshairProfile
            {
                Name  = MakeUnique(p.Name, xhNames),
                Style = xh.Clone(),
            };
            if (p.Settings.Hotkeys.TryGetValue("ToggleCrosshair", out var hb))
                slot.ToggleHotkey = new HotkeyBinding { Modifiers = hb.Modifiers, VirtualKey = hb.VirtualKey };
            _xhProfiles.Add(slot);
            profileXhSlot[p.Name] = slot.Name;
        }

        // Auto-switch rules → Game Profiles rules. The zoom slot carries over 1:1; the
        // crosshair slot follows only where that profile had a crosshair enabled.
        if (old.AppAssignments is not null)
        {
            _appRules = old.AppAssignments.Select(a => new AppRule
            {
                ProcessName      = a.ProcessName,
                DisplayName      = a.DisplayName,
                ZoomProfile      = a.ProfileName,
                CrosshairProfile = profileXhSlot.GetValueOrDefault(a.ProfileName, string.Empty),
            }).ToList();
            DefaultZoomProfile = NullIfEmpty(old.DefaultProfile);
        }
        else
        {
            // Pre-v3 file: preserve the old whitelist semantics, as v3's migration did.
            var active = old.Profiles.FirstOrDefault(p => p.Name == old.ActiveProfile) ?? old.Profiles[0];
            if (active.Settings.ProcessAwareActivation && active.Settings.Whitelist.Count > 0)
            {
                _appRules = active.Settings.Whitelist.Select(w => new AppRule
                {
                    ProcessName = w.ProcessName,
                    DisplayName = w.DisplayLabel,
                    ZoomProfile = active.Name,
                }).ToList();
                DefaultZoomProfile = null;
            }
            else
            {
                _appRules          = [];
                DefaultZoomProfile = old.ActiveProfile;
            }
        }
        DefaultCrosshairProfile = old.DefaultProfile is { Length: > 0 } dp
            ? profileXhSlot.GetValueOrDefault(dp)
            : null;

        ActiveZoomProfileName      = old.ActiveProfile;
        ActiveCrosshairProfileName = profileXhSlot.GetValueOrDefault(old.ActiveProfile, _xhProfiles[0].Name);

        // The old "Open settings" bind was per-profile; adopt the active profile's.
        var activeOld = old.Profiles.FirstOrDefault(p => p.Name == old.ActiveProfile) ?? old.Profiles[0];
        if (activeOld.Settings.Hotkeys.TryGetValue("OpenSettings", out var os) && !os.IsEmpty)
            _openSettingsHotkey = new HotkeyBinding { Modifiers = os.Modifiers, VirtualKey = os.VirtualKey };
    }

    private void InitDefault()
    {
        _zoomProfiles = [new ZoomProfile { Name = DefaultZoomName }];
        _xhProfiles   = CrosshairPresets.DefaultProfiles();
        _acProfiles   = AutoClickerPresets.DefaultProfiles();
        _appRules     = [];
        ActiveZoomProfileName        = DefaultZoomName;
        ActiveCrosshairProfileName   = _xhProfiles[0].Name;
        ActiveAutoClickerProfileName = _acProfiles[0].Name;
        DefaultZoomProfile        = DefaultZoomName;   // zoom works everywhere by default
        DefaultCrosshairProfile   = null;              // crosshair only where a rule asks for it
        DefaultAutoClickerProfile = null;              // auto-clicker off unless a rule asks for it
        _openSettingsHotkey       = DefaultOpenSettingsHotkey();
    }

    /// <summary>Repairs anything a hand-edited or older file might get wrong.</summary>
    private void EnsureValid()
    {
        if (_zoomProfiles.Count == 0) _zoomProfiles.Add(new ZoomProfile { Name = DefaultZoomName });
        if (_xhProfiles.Count   == 0) _xhProfiles = CrosshairPresets.DefaultProfiles();
        if (_acProfiles.Count   == 0) _acProfiles = AutoClickerPresets.DefaultProfiles();

        var seenZoom = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in _zoomProfiles)
        {
            if (string.IsNullOrWhiteSpace(p.Name) || !seenZoom.Add(p.Name))
                p.Name = MakeUnique(string.IsNullOrWhiteSpace(p.Name) ? DefaultZoomName : p.Name, seenZoom);
            Normalize(p.Settings);
        }

        var seenXh = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in _xhProfiles)
        {
            if (string.IsNullOrWhiteSpace(p.Name) || !seenXh.Add(p.Name))
                p.Name = MakeUnique(string.IsNullOrWhiteSpace(p.Name) ? "Crosshair" : p.Name, seenXh);
            p.Style          ??= new CrosshairSettings();
            p.ToggleHotkey   ??= CrosshairProfile.DefaultToggleHotkey();
            p.HoldHideHotkey ??= new HotkeyBinding();
        }

        var seenAc = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in _acProfiles)
        {
            if (string.IsNullOrWhiteSpace(p.Name) || !seenAc.Add(p.Name))
                p.Name = MakeUnique(string.IsNullOrWhiteSpace(p.Name) ? "Auto clicker" : p.Name, seenAc);
            p.Settings        ??= new AutoClickerSettings();
            p.Settings.ActionKey ??= new HotkeyBinding { VirtualKey = 0x01 };
            p.StartStopHotkey ??= AutoClickerProfile.DefaultStartStopHotkey();
        }

        if (!_zoomProfiles.Any(p => p.Name == ActiveZoomProfileName))
            ActiveZoomProfileName = _zoomProfiles[0].Name;
        if (!_xhProfiles.Any(p => p.Name == ActiveCrosshairProfileName))
            ActiveCrosshairProfileName = _xhProfiles[0].Name;
        if (!_acProfiles.Any(p => p.Name == ActiveAutoClickerProfileName))
            ActiveAutoClickerProfileName = _acProfiles[0].Name;

        _openSettingsHotkey ??= DefaultOpenSettingsHotkey();
    }

    /// <summary>Backfills missing zoom keybinds and coerces the engine (see memory notes).</summary>
    private static void Normalize(ZoomSettings s)
    {
        foreach (var (name, binding) in new ZoomSettings().Hotkeys)
            s.Hotkeys.TryAdd(name, binding);

        // Only the Fullscreen ("Smooth – all screens") engine ships now.
        s.Engine = MagnifierEngine.Fullscreen;
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;

    // ── Live composition ─────────────────────────────────────────────────────

    /// <summary>
    /// Refills the live <see cref="Settings"/> in place from the active zoom slot, active
    /// crosshair slot and the global Open-settings bind (see the live-instance invariant).
    /// </summary>
    private void ComposeLive()
    {
        Settings.Zoom.CopyFrom(ActiveZoom.Settings);
        Settings.Crosshair.CopyFrom(ActiveCrosshair.Style);
        Settings.AutoClicker.CopyFrom(ActiveAutoClicker.Settings);

        Settings.Hotkeys.Clear();
        foreach (var (name, b) in Settings.Zoom.Hotkeys)
            Settings.Hotkeys[name] = b;
        Settings.Hotkeys["ToggleCrosshair"]      = ActiveCrosshair.ToggleHotkey;
        Settings.Hotkeys["HoldHideCrosshair"]    = ActiveCrosshair.HoldHideHotkey;
        Settings.Hotkeys["AutoClickerStartStop"] = ActiveAutoClicker.StartStopHotkey;
        Settings.Hotkeys["OpenSettings"]         = _openSettingsHotkey;
    }

    // ── Save ───────────────────────────────────────────────────────────────────

    public void Save()
    {
        try
        {
            // Push the live values back into the active slots before writing.
            ActiveZoom.Settings.CopyFrom(Settings.Zoom);
            ActiveCrosshair.Style.CopyFrom(Settings.Crosshair);
            ActiveAutoClicker.Settings.CopyFrom(Settings.AutoClicker);

            Directory.CreateDirectory(ConfigDir);
            var file = new ConfigFile
            {
                Version                   = 5,
                ZoomProfiles              = _zoomProfiles,
                CrosshairProfiles         = _xhProfiles,
                AutoClickerProfiles       = _acProfiles,
                ActiveZoomProfile         = ActiveZoomProfileName,
                ActiveCrosshairProfile    = ActiveCrosshairProfileName,
                ActiveAutoClickerProfile  = ActiveAutoClickerProfileName,
                AppRules                  = _appRules,
                DefaultZoomProfile        = DefaultZoomProfile,
                DefaultCrosshairProfile   = DefaultCrosshairProfile,
                DefaultAutoClickerProfile = DefaultAutoClickerProfile,
                OpenSettingsHotkey        = _openSettingsHotkey,
                Updates                   = Updates,
            };
            File.WriteAllText(ConfigPath, JsonSerializer.Serialize(file, JsonOpts));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ConfigStore] Save failed: {ex.Message}");
        }
    }

    // ── Zoom slot management ────────────────────────────────────────────────────

    public bool ZoomProfileExists(string name) =>
        _zoomProfiles.Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Independent deep copy of one zoom slot, for the settings window to edit.</summary>
    public ZoomSettings GetZoomSettingsCopy(string name)
    {
        var p = _zoomProfiles.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
        if (p is null) return new ZoomSettings();
        // The active slot's authoritative values live in the shared instance, not the list copy.
        return (string.Equals(p.Name, ActiveZoomProfileName, StringComparison.OrdinalIgnoreCase)
            ? Settings.Zoom : p.Settings).Clone();
    }

    /// <summary>Writes <paramref name="settings"/> into the named zoom slot, creating it if needed.</summary>
    public void SaveZoomProfile(string name, ZoomSettings settings, bool makeActive)
    {
        if (string.IsNullOrWhiteSpace(name)) return;

        var copy = settings.Clone();
        Normalize(copy);

        var existing = _zoomProfiles.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
        {
            existing = new ZoomProfile { Name = name, Settings = copy };
            _zoomProfiles.Add(existing);
        }
        else
        {
            existing.Settings = copy;
        }

        if (makeActive)
        {
            ActiveZoomProfileName = existing.Name;
            ComposeLive();
        }

        Save();
        ProfilesChanged?.Invoke();
    }

    public void CreateZoomProfile(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || ZoomProfileExists(name)) return;
        var s = new ZoomSettings();
        Normalize(s);
        _zoomProfiles.Add(new ZoomProfile { Name = name, Settings = s });
        Save();
        ProfilesChanged?.Invoke();
    }

    public bool RenameZoomProfile(string oldName, string newName)
    {
        if (string.IsNullOrWhiteSpace(newName)) return false;
        var p = _zoomProfiles.FirstOrDefault(x => string.Equals(x.Name, oldName, StringComparison.OrdinalIgnoreCase));
        if (p is null) return false;
        if (_zoomProfiles.Any(x => x != p && string.Equals(x.Name, newName, StringComparison.OrdinalIgnoreCase)))
            return false;

        p.Name = newName;
        if (string.Equals(ActiveZoomProfileName, oldName, StringComparison.OrdinalIgnoreCase))
            ActiveZoomProfileName = newName;
        foreach (var r in _appRules)
            if (string.Equals(r.ZoomProfile, oldName, StringComparison.OrdinalIgnoreCase))
                r.ZoomProfile = newName;
        if (string.Equals(DefaultZoomProfile, oldName, StringComparison.OrdinalIgnoreCase))
            DefaultZoomProfile = newName;

        Save();
        ProfilesChanged?.Invoke();
        return true;
    }

    /// <summary>Deletes a zoom slot (never the last one). Rules pointing at it fall back to off.</summary>
    public void DeleteZoomProfile(string name)
    {
        if (_zoomProfiles.Count <= 1) return;
        var p = _zoomProfiles.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
        if (p is null) return;

        bool wasActive = string.Equals(name, ActiveZoomProfileName, StringComparison.OrdinalIgnoreCase);
        _zoomProfiles.Remove(p);
        if (wasActive)
        {
            ActiveZoomProfileName = _zoomProfiles[0].Name;
            ComposeLive();
        }

        foreach (var r in _appRules)
            if (string.Equals(r.ZoomProfile, name, StringComparison.OrdinalIgnoreCase))
                r.ZoomProfile = string.Empty;
        if (string.Equals(DefaultZoomProfile, name, StringComparison.OrdinalIgnoreCase))
            DefaultZoomProfile = null;

        Save();
        ProfilesChanged?.Invoke();
    }

    /// <summary>Makes a zoom slot live and persists the choice (tray / settings window).</summary>
    public void SwitchZoomProfile(string name)
    {
        var target = _zoomProfiles.FirstOrDefault(p => p.Name == name);
        if (target is null || name == ActiveZoomProfileName) return;

        // Persist the current live edits into the outgoing slot first.
        ActiveZoom.Settings.CopyFrom(Settings.Zoom);
        ActiveZoomProfileName = name;
        ComposeLive();

        Save();
        ProfilesChanged?.Invoke();
    }

    /// <summary>Makes a zoom slot live without persisting (foreground auto-switcher).</summary>
    public void ActivateZoomRuntime(string name)
    {
        var target = _zoomProfiles.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        if (target is null || string.Equals(name, ActiveZoomProfileName, StringComparison.OrdinalIgnoreCase)) return;

        ActiveZoomProfileName = target.Name;
        ComposeLive();
        ProfilesChanged?.Invoke();
    }

    // ── Crosshair slot management ───────────────────────────────────────────────

    public bool CrosshairProfileExists(string name) =>
        _xhProfiles.Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Independent deep copy of one crosshair slot's style.</summary>
    public CrosshairSettings GetCrosshairStyleCopy(string name)
    {
        var p = _xhProfiles.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
        if (p is null) return new CrosshairSettings();
        return (string.Equals(p.Name, ActiveCrosshairProfileName, StringComparison.OrdinalIgnoreCase)
            ? Settings.Crosshair : p.Style).Clone();
    }

    /// <summary>Copy of one crosshair slot's show/hide keybind.</summary>
    public HotkeyBinding GetCrosshairHotkeyCopy(string name)
    {
        var p = _xhProfiles.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
        var b = p?.ToggleHotkey ?? CrosshairProfile.DefaultToggleHotkey();
        return new HotkeyBinding { Modifiers = b.Modifiers, VirtualKey = b.VirtualKey };
    }

    /// <summary>Copy of one crosshair slot's "hide while held" keybind.</summary>
    public HotkeyBinding GetCrosshairHoldHideHotkeyCopy(string name)
    {
        var p = _xhProfiles.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
        var b = p?.HoldHideHotkey ?? new HotkeyBinding();
        return new HotkeyBinding { Modifiers = b.Modifiers, VirtualKey = b.VirtualKey };
    }

    /// <summary>Writes a style + keybinds into the named crosshair slot, creating it if needed.</summary>
    public void SaveCrosshairProfile(string name, CrosshairSettings style,
        HotkeyBinding toggleHotkey, HotkeyBinding holdHideHotkey, bool makeActive)
    {
        if (string.IsNullOrWhiteSpace(name)) return;

        var existing = _xhProfiles.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
        {
            existing = new CrosshairProfile { Name = name };
            _xhProfiles.Add(existing);
        }
        existing.Style          = style.Clone();
        existing.ToggleHotkey   = new HotkeyBinding { Modifiers = toggleHotkey.Modifiers, VirtualKey = toggleHotkey.VirtualKey };
        existing.HoldHideHotkey = new HotkeyBinding { Modifiers = holdHideHotkey.Modifiers, VirtualKey = holdHideHotkey.VirtualKey };

        if (makeActive)
        {
            ActiveCrosshairProfileName = existing.Name;
            ComposeLive();
        }

        Save();
        ProfilesChanged?.Invoke();
    }

    public void CreateCrosshairProfile(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || CrosshairProfileExists(name)) return;
        _xhProfiles.Add(new CrosshairProfile { Name = name });
        Save();
        ProfilesChanged?.Invoke();
    }

    public bool RenameCrosshairProfile(string oldName, string newName)
    {
        if (string.IsNullOrWhiteSpace(newName)) return false;
        var p = _xhProfiles.FirstOrDefault(x => string.Equals(x.Name, oldName, StringComparison.OrdinalIgnoreCase));
        if (p is null) return false;
        if (_xhProfiles.Any(x => x != p && string.Equals(x.Name, newName, StringComparison.OrdinalIgnoreCase)))
            return false;

        p.Name = newName;
        if (string.Equals(ActiveCrosshairProfileName, oldName, StringComparison.OrdinalIgnoreCase))
            ActiveCrosshairProfileName = newName;
        foreach (var r in _appRules)
            if (string.Equals(r.CrosshairProfile, oldName, StringComparison.OrdinalIgnoreCase))
                r.CrosshairProfile = newName;
        if (string.Equals(DefaultCrosshairProfile, oldName, StringComparison.OrdinalIgnoreCase))
            DefaultCrosshairProfile = newName;

        Save();
        ProfilesChanged?.Invoke();
        return true;
    }

    /// <summary>Deletes a crosshair slot (never the last one). Rules pointing at it fall back to off.</summary>
    public void DeleteCrosshairProfile(string name)
    {
        if (_xhProfiles.Count <= 1) return;
        var p = _xhProfiles.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
        if (p is null) return;

        bool wasActive = string.Equals(name, ActiveCrosshairProfileName, StringComparison.OrdinalIgnoreCase);
        _xhProfiles.Remove(p);
        if (wasActive)
        {
            ActiveCrosshairProfileName = _xhProfiles[0].Name;
            ComposeLive();
        }

        foreach (var r in _appRules)
            if (string.Equals(r.CrosshairProfile, name, StringComparison.OrdinalIgnoreCase))
                r.CrosshairProfile = string.Empty;
        if (string.Equals(DefaultCrosshairProfile, name, StringComparison.OrdinalIgnoreCase))
            DefaultCrosshairProfile = null;

        Save();
        ProfilesChanged?.Invoke();
    }

    /// <summary>Makes a crosshair slot live and persists the choice (tray / settings window).</summary>
    public void SwitchCrosshairProfile(string name)
    {
        var target = _xhProfiles.FirstOrDefault(p => p.Name == name);
        if (target is null || name == ActiveCrosshairProfileName) return;

        ActiveCrosshair.Style.CopyFrom(Settings.Crosshair);
        ActiveCrosshairProfileName = name;
        ComposeLive();

        Save();
        ProfilesChanged?.Invoke();
    }

    /// <summary>Makes a crosshair slot live without persisting (foreground auto-switcher).</summary>
    public void ActivateCrosshairRuntime(string name)
    {
        var target = _xhProfiles.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        if (target is null || string.Equals(name, ActiveCrosshairProfileName, StringComparison.OrdinalIgnoreCase)) return;

        ActiveCrosshairProfileName = target.Name;
        ComposeLive();
        ProfilesChanged?.Invoke();
    }

    // ── Auto-clicker slot management ─────────────────────────────────────────────

    public bool AutoClickerProfileExists(string name) =>
        _acProfiles.Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Independent deep copy of one auto-clicker slot's settings.</summary>
    public AutoClickerSettings GetAutoClickerSettingsCopy(string name)
    {
        var p = _acProfiles.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
        if (p is null) return new AutoClickerSettings();
        return (string.Equals(p.Name, ActiveAutoClickerProfileName, StringComparison.OrdinalIgnoreCase)
            ? Settings.AutoClicker : p.Settings).Clone();
    }

    /// <summary>Copy of one auto-clicker slot's start/stop keybind.</summary>
    public HotkeyBinding GetAutoClickerHotkeyCopy(string name)
    {
        var p = _acProfiles.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
        var b = p?.StartStopHotkey ?? AutoClickerProfile.DefaultStartStopHotkey();
        return new HotkeyBinding { Modifiers = b.Modifiers, VirtualKey = b.VirtualKey };
    }

    /// <summary>Writes settings + keybind into the named auto-clicker slot, creating it if needed.</summary>
    public void SaveAutoClickerProfile(string name, AutoClickerSettings settings, HotkeyBinding startStopHotkey, bool makeActive)
    {
        if (string.IsNullOrWhiteSpace(name)) return;

        var existing = _acProfiles.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
        {
            existing = new AutoClickerProfile { Name = name };
            _acProfiles.Add(existing);
        }
        existing.Settings        = settings.Clone();
        existing.StartStopHotkey = new HotkeyBinding { Modifiers = startStopHotkey.Modifiers, VirtualKey = startStopHotkey.VirtualKey };

        if (makeActive)
        {
            ActiveAutoClickerProfileName = existing.Name;
            ComposeLive();
        }

        Save();
        ProfilesChanged?.Invoke();
    }

    public void CreateAutoClickerProfile(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || AutoClickerProfileExists(name)) return;
        _acProfiles.Add(new AutoClickerProfile { Name = name });
        Save();
        ProfilesChanged?.Invoke();
    }

    public bool RenameAutoClickerProfile(string oldName, string newName)
    {
        if (string.IsNullOrWhiteSpace(newName)) return false;
        var p = _acProfiles.FirstOrDefault(x => string.Equals(x.Name, oldName, StringComparison.OrdinalIgnoreCase));
        if (p is null) return false;
        if (_acProfiles.Any(x => x != p && string.Equals(x.Name, newName, StringComparison.OrdinalIgnoreCase)))
            return false;

        p.Name = newName;
        if (string.Equals(ActiveAutoClickerProfileName, oldName, StringComparison.OrdinalIgnoreCase))
            ActiveAutoClickerProfileName = newName;
        foreach (var r in _appRules)
            if (string.Equals(r.AutoClickerProfile, oldName, StringComparison.OrdinalIgnoreCase))
                r.AutoClickerProfile = newName;
        if (string.Equals(DefaultAutoClickerProfile, oldName, StringComparison.OrdinalIgnoreCase))
            DefaultAutoClickerProfile = newName;

        Save();
        ProfilesChanged?.Invoke();
        return true;
    }

    /// <summary>Deletes an auto-clicker slot (never the last one). Rules pointing at it fall back to off.</summary>
    public void DeleteAutoClickerProfile(string name)
    {
        if (_acProfiles.Count <= 1) return;
        var p = _acProfiles.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
        if (p is null) return;

        bool wasActive = string.Equals(name, ActiveAutoClickerProfileName, StringComparison.OrdinalIgnoreCase);
        _acProfiles.Remove(p);
        if (wasActive)
        {
            ActiveAutoClickerProfileName = _acProfiles[0].Name;
            ComposeLive();
        }

        foreach (var r in _appRules)
            if (string.Equals(r.AutoClickerProfile, name, StringComparison.OrdinalIgnoreCase))
                r.AutoClickerProfile = string.Empty;
        if (string.Equals(DefaultAutoClickerProfile, name, StringComparison.OrdinalIgnoreCase))
            DefaultAutoClickerProfile = null;

        Save();
        ProfilesChanged?.Invoke();
    }

    /// <summary>Makes an auto-clicker slot live and persists the choice (tray / settings window).</summary>
    public void SwitchAutoClickerProfile(string name)
    {
        var target = _acProfiles.FirstOrDefault(p => p.Name == name);
        if (target is null || name == ActiveAutoClickerProfileName) return;

        ActiveAutoClicker.Settings.CopyFrom(Settings.AutoClicker);
        ActiveAutoClickerProfileName = name;
        ComposeLive();

        Save();
        ProfilesChanged?.Invoke();
    }

    /// <summary>Makes an auto-clicker slot live without persisting (foreground auto-switcher).</summary>
    public void ActivateAutoClickerRuntime(string name)
    {
        var target = _acProfiles.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        if (target is null || string.Equals(name, ActiveAutoClickerProfileName, StringComparison.OrdinalIgnoreCase)) return;

        ActiveAutoClickerProfileName = target.Name;
        ComposeLive();
        ProfilesChanged?.Invoke();
    }

    // ── Game Profiles rules ─────────────────────────────────────────────────────

    /// <summary>
    /// Resolves the zoom, crosshair and auto-clicker slots for a foreground process: the app's
    /// rule when listed, else the defaults. Null in a position means that feature is off (no slot
    /// / explicit off / the named slot no longer exists).
    /// </summary>
    public (string? ZoomSlot, string? CrosshairSlot, string? AutoClickerSlot) ResolveForProcess(string? processName)
    {
        string? zoom, xh, ac;
        if (processName is not null &&
            _appRules.FirstOrDefault(x => string.Equals(x.ProcessName, processName, StringComparison.OrdinalIgnoreCase)) is { } r)
        {
            zoom = r.ZoomProfile;        // empty => explicitly off
            xh   = r.CrosshairProfile;
            ac   = r.AutoClickerProfile;
        }
        else
        {
            zoom = DefaultZoomProfile;
            xh   = DefaultCrosshairProfile;
            ac   = DefaultAutoClickerProfile;
        }

        if (string.IsNullOrEmpty(zoom) || !ZoomProfileExists(zoom))         zoom = null;
        if (string.IsNullOrEmpty(xh)   || !CrosshairProfileExists(xh))      xh   = null;
        if (string.IsNullOrEmpty(ac)   || !AutoClickerProfileExists(ac))    ac   = null;
        return (zoom, xh, ac);
    }

    /// <summary>Adds or updates an app rule. Empty/null slot names mean off for that feature.</summary>
    public void SetAppRule(string processName, string displayName, string? zoomProfile,
        string? crosshairProfile, string? autoClickerProfile)
    {
        if (string.IsNullOrWhiteSpace(processName)) return;

        var r = _appRules.FirstOrDefault(x => string.Equals(x.ProcessName, processName, StringComparison.OrdinalIgnoreCase));
        if (r is null)
        {
            r = new AppRule { ProcessName = processName };
            _appRules.Add(r);
        }
        r.DisplayName        = string.IsNullOrWhiteSpace(displayName) ? processName : displayName;
        r.ZoomProfile        = zoomProfile ?? string.Empty;
        r.CrosshairProfile   = crosshairProfile ?? string.Empty;
        r.AutoClickerProfile = autoClickerProfile ?? string.Empty;

        Save();
    }

    public void RemoveAppRule(string processName)
    {
        _appRules.RemoveAll(x => string.Equals(x.ProcessName, processName, StringComparison.OrdinalIgnoreCase));
        Save();
    }

    public void SetDefaultZoomProfile(string? name)
    {
        DefaultZoomProfile = NullIfEmpty(name);
        Save();
    }

    public void SetDefaultCrosshairProfile(string? name)
    {
        DefaultCrosshairProfile = NullIfEmpty(name);
        Save();
    }

    public void SetDefaultAutoClickerProfile(string? name)
    {
        DefaultAutoClickerProfile = NullIfEmpty(name);
        Save();
    }

    // ── Global hotkey ───────────────────────────────────────────────────────────

    /// <summary>Updates the app-wide Open-settings bind (applied immediately, not slot-scoped).</summary>
    public void SetOpenSettingsHotkey(HotkeyBinding binding)
    {
        _openSettingsHotkey = new HotkeyBinding { Modifiers = binding.Modifiers, VirtualKey = binding.VirtualKey };
        ComposeLive();
        Save();
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    /// <summary>Returns <paramref name="name"/>, or "name (2)", "name (3)"… if it collides with <paramref name="taken"/>.</summary>
    private static string MakeUnique(string name, HashSet<string> taken)
    {
        if (taken.Add(name)) return name;
        for (int i = 2; ; i++)
        {
            var candidate = $"{name} ({i})";
            if (taken.Add(candidate)) return candidate;
        }
    }
}
