using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Oversight.Linux.Platform;
using Oversight.Linux.Services;
using Oversight.Models;
using Oversight.Services;

namespace Oversight.Linux.Ui;

/// <summary>
/// The settings window, a port of the Windows one. Zoom, crosshair and auto clicker are independent documents: each page
/// edits one open save slot (always the live one of its kind) with its own unsaved flag and Save / Save as buttons. Game
/// Profiles and the app-wide bits (startup, the Open settings keybind, updates) apply immediately.
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly ConfigStore _config;
    private readonly Updater _updater;
    private readonly HotkeyManager _hotkeys;
    private readonly ProcessWatcher _watcher;
    private readonly CrosshairService _crosshair;

    private ZoomSettings _workingZoom = new();
    private string _openZoomName = "";
    private CrosshairSettings _workingXh = new();
    private string _openXhName = "";
    private AutoClickerSettings _workingAc = new();
    private string _openAcName = "";
    private bool _zoomDirty, _xhDirty, _acDirty;

    private bool _loading = true;        // suppress dirty-marking while populating controls
    private bool _suppressZoomCombo, _suppressXhCombo, _suppressAcCombo, _loadingRules, _syncingColor;
    private bool _onScreen, _closeConfirmed, _ready;

    /// <summary>Set before closing the window on app shutdown, so we don't prompt mid-quit.</summary>
    public bool SuppressUnsavedPrompt { get; set; }

    public SettingsWindow() : this(null!, null!, null!, null!, null!) { }   // for the XAML previewer only

    public SettingsWindow(ConfigStore config, HotkeyManager hotkeys, ProcessWatcher watcher, CrosshairService crosshair, Updater updater)
    {
        _config = config;
        _hotkeys = hotkeys;
        _watcher = watcher;
        _crosshair = crosshair;
        _updater = updater;
        InitializeComponent();
        if (config is null) return;
        _ready = true;   // sliders report value changes while the XAML loads, before the named controls exist

        var v = Updater.CurrentVersion;
        TxtSidebarVersion.Text = TxtAboutVersion.Text = $"Version {v.Major}.{v.Minor}.{v.Build}";
        ZoomUnsupported.IsVisible = !Magnifier.Supported;
        if (!X11.Available)
        {
            X11Note.IsVisible = true;
            TxtX11Note.Text = "Hotkeys, the crosshair and the auto clicker need an X11 session or XWayland, and there's no X display.";
        }
        else if (Environment.GetEnvironmentVariable("XDG_SESSION_TYPE") == "wayland")
        {
            X11Note.IsVisible = true;
            TxtX11Note.Text = "On Wayland, hotkeys, the crosshair and the auto clicker work while an X11 app is focused, which includes most games through Steam and Proton.";
        }

        // Pause foreground-driven slot switching while the window is on screen, so editing a slot isn't undone by the
        // settings window itself being focused; resume when hidden, minimized or closed.
        PropertyChanged += (_, e) =>
        {
            if (e.Property == IsVisibleProperty || e.Property == WindowStateProperty) UpdateOnScreen();
        };
        Opened += (_, _) => UpdateOnScreen();
        Closed += (_, _) =>
        {
            _crosshair.SetPreview(null);
            if (_onScreen) _watcher.ResumeAutoSwitch();
            _onScreen = false;
        };

        // Hotkeys stay live while the window is open; only the instant a key box is recording are they released.
        foreach (var box in new[] { HkToggle, HkZoomIn, HkZoomOut, HkCrosshair, HkHoldHideCrosshair, HkAcAction, HkAcStartStop, HkOpenSettings })
        {
            box.CaptureStarted += _hotkeys.Suspend;
            box.CaptureStopped += _hotkeys.Resume;
        }
        foreach (var box in new[] { HkToggle, HkZoomIn, HkZoomOut })
            box.BindingValueChanged += (_, _) => MarkZoomDirty();
        HkCrosshair.BindingValueChanged += (_, _) => MarkXhDirty();
        HkHoldHideCrosshair.BindingValueChanged += (_, _) => MarkXhDirty();
        HkAcAction.BindingValueChanged += (_, _) => { UpdateAcConditionalRows(); MarkAcDirty(); };
        HkAcStartStop.BindingValueChanged += (_, _) => MarkAcDirty();
        HkOpenSettings.BindingValueChanged += (_, _) => ApplyOpenSettingsHotkey();

        LoadInitial();
    }

    private void UpdateOnScreen()
    {
        bool onScreen = IsVisible && WindowState != WindowState.Minimized;
        if (onScreen == _onScreen) return;
        _onScreen = onScreen;
        if (onScreen)
        {
            _watcher.SuspendAutoSwitch();
            PushOrClearLivePreview();
        }
        else
        {
            _crosshair.SetPreview(null);
            _watcher.ResumeAutoSwitch();
        }
    }

    public void ShowPage(string tag)
    {
        Nav.SelectedItem = Nav.Items.OfType<ListBoxItem>().FirstOrDefault(i => (string?)i.Tag == tag) ?? Nav.Items.OfType<ListBoxItem>().First();
    }

    private void Nav_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        string tag = (Nav.SelectedItem as ListBoxItem)?.Tag as string ?? "zoom";
        PageZoom.IsVisible = tag == "zoom";
        PageXh.IsVisible = tag == "crosshair";
        PageAc.IsVisible = tag == "autoclicker";
        PageGames.IsVisible = tag == "games";
        PageGeneral.IsVisible = tag == "general";
        if (_config is not null) PushOrClearLivePreview();
    }

    // ── Initial load ────────────────────────────────────────────────────────────

    private void LoadInitial()
    {
        CbActivation.ItemsSource = ActivationChoices;
        CbXhShape.ItemsSource = ShapeChoices;
        CbXhColor.ItemsSource = ColorChoices;
        CbAcClickType.ItemsSource = ClickTypeChoices;
        CbAcRepeat.ItemsSource = RepeatChoices;
        CbAcActivation.ItemsSource = ActivationChoices;

        _loading = true;
        ChkStartup.IsChecked = Autostart.IsEnabled;
        HkOpenSettings.BindingValue = Clone(_config.OpenSettingsHotkey);
        ChkHotkeyWarnings.IsChecked = _config.ShowHotkeyWarnings;
        ChkCheckUpdates.IsChecked = _config.Updates.CheckForUpdates;
        ChkAutoInstall.IsChecked = _config.Updates.InstallAutomatically;
        ChkAutoInstall.IsEnabled = _config.Updates.CheckForUpdates;
        RenderUpdate();
        _loading = false;

        LoadZoomProfile(_config.ActiveZoomProfileName);
        LoadXhProfile(_config.ActiveCrosshairProfileName);
        LoadAcProfile(_config.ActiveAutoClickerProfileName);
        RebuildGameProfilesTab();
        Nav.SelectedIndex = 0;
    }

    private void ReloadServices()
    {
        _hotkeys.ReloadBindings();
        _watcher.Reload();
    }

    // ═════════════════════════ Zoom document ═════════════════════════

    private void LoadZoomProfile(string name)
    {
        _openZoomName = name;
        _workingZoom = _config.GetZoomSettingsCopy(name);
        _loading = true;
        CbActivation.SelectedItem = ActivationChoices.FirstOrDefault(c => c.Mode == _workingZoom.ToggleZoomMode) ?? ActivationChoices[0];
        SliderZoom.Value = _workingZoom.DefaultZoomLevel;
        SliderStep.Value = _workingZoom.ZoomStep;
        TxtZoom.Text = $"{_workingZoom.DefaultZoomLevel:F2}×";
        TxtStep.Text = $"{_workingZoom.ZoomStep:F2}";
        HkToggle.BindingValue = Clone(_workingZoom.Hotkeys.GetValueOrDefault("ToggleZoom", new()));
        HkZoomIn.BindingValue = Clone(_workingZoom.Hotkeys.GetValueOrDefault("ZoomIn", new()));
        HkZoomOut.BindingValue = Clone(_workingZoom.Hotkeys.GetValueOrDefault("ZoomOut", new()));
        _loading = false;
        SetZoomDirty(false);
        RefreshCombo(CbProfile, _config.ZoomProfileNames, _openZoomName, v => _suppressZoomCombo = v);
        BtnDeleteProfile.IsEnabled = _config.ZoomProfileNames.Count > 1;
    }

    // The animation and panning values have no control here (the desktop magnifier doesn't do them); they stay as saved.
    private void CommitZoomControls(ZoomSettings s)
    {
        if (CbActivation.SelectedItem is ActivationChoice activation) s.ToggleZoomMode = activation.Mode;
        s.DefaultZoomLevel = (float)SliderZoom.Value;
        s.ZoomStep = (float)SliderStep.Value;
        s.Hotkeys["ToggleZoom"] = HkToggle.BindingValue ?? new();
        s.Hotkeys["ZoomIn"] = HkZoomIn.BindingValue ?? new();
        s.Hotkeys["ZoomOut"] = HkZoomOut.BindingValue ?? new();
    }

    private void MarkZoomDirty() { if (!_loading) SetZoomDirty(true); }
    private void SetZoomDirty(bool v) { _zoomDirty = v; UpdateDirtyIndicators(); }

    private void SliderZoom_Changed(object? sender, RangeBaseValueChangedEventArgs e)
    {
        if (!_ready) return;
        TxtZoom.Text = $"{e.NewValue:F2}×";
        MarkZoomDirty();
    }

    private void SliderStep_Changed(object? sender, RangeBaseValueChangedEventArgs e)
    {
        if (!_ready) return;
        TxtStep.Text = $"{e.NewValue:F2}";
        MarkZoomDirty();
    }

    private void ZoomControl_Changed(object? sender, RoutedEventArgs e) => MarkZoomDirty();

    private async void CbProfile_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressZoomCombo || CbProfile.SelectedItem is not string name || name == _openZoomName) return;
        if (!await EnsureZoomSavedOrDiscarded())
        {
            RefreshCombo(CbProfile, _config.ZoomProfileNames, _openZoomName, v => _suppressZoomCombo = v);
            return;
        }
        _config.SwitchZoomProfile(name);
        LoadZoomProfile(name);
        ReloadServices();
    }

    private async void SaveProfile_Click(object? sender, RoutedEventArgs e) => await DoSaveZoom();

    private async Task<bool> DoSaveZoom()
    {
        CommitZoomControls(_workingZoom);
        if (await CheckConflicts()) return false;
        _config.SaveZoomProfile(_openZoomName, _workingZoom, makeActive: true);
        ReloadServices();
        SetZoomDirty(false);
        return true;
    }

    private async void SaveAsProfile_Click(object? sender, RoutedEventArgs e)
    {
        CommitZoomControls(_workingZoom);
        if (await CheckConflicts()) return;
        if (await Dialogs.SaveAsAsync(this, _config.ZoomProfileNames, SuggestName(_openZoomName + " copy", _config.ZoomProfileNames)) is not string name) return;
        _config.SaveZoomProfile(name, _workingZoom, makeActive: true);
        LoadZoomProfile(StoredName(name, _config.ZoomProfileNames));
        RebuildGameProfilesTab();
        ReloadServices();
    }

    private async void NewProfile_Click(object? sender, RoutedEventArgs e)
    {
        if (!await EnsureZoomSavedOrDiscarded()) return;
        if (await PromptName("New zoom slot", "Name for the new zoom slot:", SuggestName("Zoom", _config.ZoomProfileNames), _config.ZoomProfileExists) is not string name) return;
        _config.CreateZoomProfile(name);
        _config.SwitchZoomProfile(name);
        LoadZoomProfile(name);
        RebuildGameProfilesTab();
        ReloadServices();
    }

    private async void RenameProfile_Click(object? sender, RoutedEventArgs e)
    {
        var name = await PromptName("Rename zoom slot", "New name:", _openZoomName, _config.ZoomProfileExists, allowName: _openZoomName);
        if (name is null || name == _openZoomName || !_config.RenameZoomProfile(_openZoomName, name)) return;
        _openZoomName = name;
        RefreshCombo(CbProfile, _config.ZoomProfileNames, _openZoomName, v => _suppressZoomCombo = v);
        RebuildGameProfilesTab();
    }

    private async void DeleteProfile_Click(object? sender, RoutedEventArgs e)
    {
        if (_config.ZoomProfileNames.Count <= 1) return;
        if (await Dialogs.AskAsync(this, $"Delete the zoom slot \"{_openZoomName}\"? This cannot be undone.", "OK", "Cancel") != 0) return;
        _config.DeleteZoomProfile(_openZoomName);
        LoadZoomProfile(_config.ActiveZoomProfileName);
        RebuildGameProfilesTab();
        ReloadServices();
    }

    private async Task<bool> EnsureZoomSavedOrDiscarded()
    {
        if (!_zoomDirty) return true;
        return await Dialogs.AskAsync(this, $"Save changes to the zoom slot \"{_openZoomName}\" first?", "Yes", "No", "Cancel") switch
        {
            0 => await DoSaveZoom(),
            1 => true,
            _ => false,
        };
    }

    // ═════════════════════════ Crosshair document ═════════════════════════

    private void LoadXhProfile(string name)
    {
        _openXhName = name;
        _workingXh = _config.GetCrosshairStyleCopy(name);
        _loading = true;
        CbXhShape.SelectedItem = ShapeChoices.FirstOrDefault(c => c.Shape == _workingXh.Shape) ?? ShapeChoices[0];
        SliderXhSize.Value = Math.Clamp(_workingXh.Size, 8, 128);
        SliderXhThickness.Value = Math.Clamp(_workingXh.Thickness, 1, 10);
        SliderXhGap.Value = Math.Clamp(_workingXh.Gap, 0, 40);
        SliderXhOpacity.Value = Math.Clamp(_workingXh.Opacity, 10, 100);
        ChkXhCenterDot.IsChecked = _workingXh.CenterDot;
        ChkXhOutline.IsChecked = _workingXh.Outline;
        TxtXhHex.Text = NormalizeHex(_workingXh.ColorHex) ?? "#00FF40";
        HkCrosshair.BindingValue = _config.GetCrosshairHotkeyCopy(name);
        HkHoldHideCrosshair.BindingValue = _config.GetCrosshairHoldHideHotkeyCopy(name);
        UpdateXhReadouts();
        _loading = false;
        SetXhDirty(false);
        RefreshCombo(CbXhProfile, _config.CrosshairProfileNames, _openXhName, v => _suppressXhCombo = v);
        BtnDeleteXh.IsEnabled = _config.CrosshairProfileNames.Count > 1;
        UpdateCrosshairPreview();
    }

    private void CommitXhControls(CrosshairSettings s)
    {
        if (CbXhShape.SelectedItem is ShapeChoice shape) s.Shape = shape.Shape;
        s.Size = (int)SliderXhSize.Value;
        s.Thickness = (int)SliderXhThickness.Value;
        s.Gap = (int)SliderXhGap.Value;
        s.Opacity = (int)SliderXhOpacity.Value;
        s.CenterDot = ChkXhCenterDot.IsChecked == true;
        s.Outline = ChkXhOutline.IsChecked == true;
        s.ColorHex = NormalizeHex(TxtXhHex.Text) ?? s.ColorHex;
    }

    private void MarkXhDirty() { if (!_loading) SetXhDirty(true); }
    private void SetXhDirty(bool v) { _xhDirty = v; UpdateDirtyIndicators(); }

    private void UpdateXhReadouts()
    {
        TxtXhSize.Text = $"{(int)SliderXhSize.Value} px";
        TxtXhThickness.Text = $"{(int)SliderXhThickness.Value} px";
        TxtXhGap.Text = $"{(int)SliderXhGap.Value} px";
        TxtXhOpacity.Text = $"{(int)SliderXhOpacity.Value} %";
    }

    private void XhSlider_Changed(object? sender, RangeBaseValueChangedEventArgs e)
    {
        if (!_ready) return;
        UpdateXhReadouts();
        OnCrosshairStyleChanged();
    }

    private void XhStyle_Changed(object? sender, RoutedEventArgs e) => OnCrosshairStyleChanged();

    private void OnCrosshairStyleChanged()
    {
        if (_loading) return;
        MarkXhDirty();
        UpdateCrosshairPreview(pushLive: true);
    }

    private void CbXhColor_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (_loading || _syncingColor || CbXhColor.SelectedItem is not ColorChoice c) return;
        _syncingColor = true;
        TxtXhHex.Text = c.Hex;   // TextChanged still fires → dirty + preview
        _syncingColor = false;
    }

    private void TxtXhHex_Changed(object? sender, TextChangedEventArgs e)
    {
        if (!_syncingColor)
        {
            _syncingColor = true;
            var norm = NormalizeHex(TxtXhHex.Text);
            CbXhColor.SelectedItem = ColorChoices.FirstOrDefault(c => string.Equals(c.Hex, norm, StringComparison.OrdinalIgnoreCase));
            _syncingColor = false;
        }
        OnCrosshairStyleChanged();
    }

    /// <summary>The on-screen preview shows only while the window is on screen with the Crosshair page open.</summary>
    private void PushOrClearLivePreview()
    {
        if (_onScreen && PageXh.IsVisible) UpdateCrosshairPreview(pushLive: true);
        else _crosshair.SetPreview(null);
    }

    private void UpdateCrosshairPreview(bool pushLive = false)
    {
        if (CbXhShape.SelectedItem is not ShapeChoice) return;
        var style = new CrosshairSettings();
        CommitXhControls(style);
        XhPreview.Style = style;
        if (pushLive && _onScreen && PageXh.IsVisible) _crosshair.SetPreview(style);
    }

    private async void CbXhProfile_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressXhCombo || CbXhProfile.SelectedItem is not string name || name == _openXhName) return;
        if (!await EnsureXhSavedOrDiscarded())
        {
            RefreshCombo(CbXhProfile, _config.CrosshairProfileNames, _openXhName, v => _suppressXhCombo = v);
            return;
        }
        _config.SwitchCrosshairProfile(name);
        LoadXhProfile(name);
        ReloadServices();
    }

    private async void SaveXh_Click(object? sender, RoutedEventArgs e) => await DoSaveXh();

    private async Task<bool> DoSaveXh()
    {
        CommitXhControls(_workingXh);
        if (await CheckConflicts()) return false;
        _config.SaveCrosshairProfile(_openXhName, _workingXh, HkCrosshair.BindingValue ?? new(), HkHoldHideCrosshair.BindingValue ?? new(), makeActive: true);
        ReloadServices();
        SetXhDirty(false);
        return true;
    }

    private async void SaveAsXh_Click(object? sender, RoutedEventArgs e)
    {
        CommitXhControls(_workingXh);
        if (await CheckConflicts()) return;
        if (await Dialogs.SaveAsAsync(this, _config.CrosshairProfileNames, SuggestName(_openXhName + " copy", _config.CrosshairProfileNames)) is not string name) return;
        _config.SaveCrosshairProfile(name, _workingXh, HkCrosshair.BindingValue ?? new(), HkHoldHideCrosshair.BindingValue ?? new(), makeActive: true);
        LoadXhProfile(StoredName(name, _config.CrosshairProfileNames));
        RebuildGameProfilesTab();
        ReloadServices();
    }

    private async void NewXh_Click(object? sender, RoutedEventArgs e)
    {
        if (!await EnsureXhSavedOrDiscarded()) return;
        if (await PromptName("New crosshair slot", "Name for the new crosshair slot:", SuggestName("Crosshair", _config.CrosshairProfileNames), _config.CrosshairProfileExists) is not string name) return;
        _config.CreateCrosshairProfile(name);
        _config.SwitchCrosshairProfile(name);
        LoadXhProfile(name);
        RebuildGameProfilesTab();
        ReloadServices();
    }

    private async void RenameXh_Click(object? sender, RoutedEventArgs e)
    {
        var name = await PromptName("Rename crosshair slot", "New name:", _openXhName, _config.CrosshairProfileExists, allowName: _openXhName);
        if (name is null || name == _openXhName || !_config.RenameCrosshairProfile(_openXhName, name)) return;
        _openXhName = name;
        RefreshCombo(CbXhProfile, _config.CrosshairProfileNames, _openXhName, v => _suppressXhCombo = v);
        RebuildGameProfilesTab();
    }

    private async void DeleteXh_Click(object? sender, RoutedEventArgs e)
    {
        if (_config.CrosshairProfileNames.Count <= 1) return;
        if (await Dialogs.AskAsync(this, $"Delete the crosshair slot \"{_openXhName}\"? This cannot be undone.", "OK", "Cancel") != 0) return;
        _config.DeleteCrosshairProfile(_openXhName);
        LoadXhProfile(_config.ActiveCrosshairProfileName);
        RebuildGameProfilesTab();
        ReloadServices();
    }

    private async Task<bool> EnsureXhSavedOrDiscarded()
    {
        if (!_xhDirty) return true;
        return await Dialogs.AskAsync(this, $"Save changes to the crosshair slot \"{_openXhName}\" first?", "Yes", "No", "Cancel") switch
        {
            0 => await DoSaveXh(),
            1 => true,
            _ => false,
        };
    }

    // ═════════════════════════ Auto-clicker document ═════════════════════════

    private void LoadAcProfile(string name)
    {
        _openAcName = name;
        _workingAc = _config.GetAutoClickerSettingsCopy(name);
        _loading = true;
        LoadAcIntoControls(_workingAc);
        HkAcStartStop.BindingValue = _config.GetAutoClickerHotkeyCopy(name);
        _loading = false;
        SetAcDirty(false);
        RefreshCombo(CbAcProfile, _config.AutoClickerProfileNames, _openAcName, v => _suppressAcCombo = v);
        BtnDeleteAc.IsEnabled = _config.AutoClickerProfileNames.Count > 1;
    }

    private void LoadAcIntoControls(AutoClickerSettings s)
    {
        HkAcAction.BindingValue = new HotkeyBinding { Modifiers = s.ActionKey.Modifiers, VirtualKey = s.ActionKey.VirtualKey };
        CbAcClickType.SelectedItem = ClickTypeChoices.FirstOrDefault(c => c.Type == s.ClickType) ?? ClickTypeChoices[0];
        CbAcRepeat.SelectedItem = RepeatChoices.FirstOrDefault(c => c.Mode == s.RepeatMode) ?? RepeatChoices[0];
        CbAcActivation.SelectedItem = ActivationChoices.FirstOrDefault(c => c.Mode == s.Activation) ?? ActivationChoices[0];
        SliderAcInterval.Value = Math.Clamp(s.IntervalMs, 10, 2000);
        ChkAcJitter.IsChecked = s.JitterEnabled;
        SliderAcJitter.Value = Math.Clamp(s.JitterMs, 0, 500);
        ChkAcAdaptive.IsChecked = s.AdaptiveLimit;
        TxtAcSeconds.Text = s.RepeatSeconds.ToString();
        TxtAcCount.Text = s.RepeatCount.ToString();
        ChkAcFixedPos.IsChecked = s.UseFixedPosition;
        TxtAcX.Text = s.FixedX.ToString();
        TxtAcY.Text = s.FixedY.ToString();
        ChkAcSafety.IsChecked = s.SafetyCountdownEnabled;
        TxtAcSafetySeconds.Text = s.SafetyCountdownSeconds.ToString();
        UpdateAcConditionalRows();
    }

    private void CommitAcControls(AutoClickerSettings s)
    {
        if (HkAcAction.BindingValue is { } ak) s.ActionKey = new HotkeyBinding { Modifiers = 0, VirtualKey = ak.VirtualKey };
        if (CbAcClickType.SelectedItem is MouseClickTypeChoice ct) s.ClickType = ct.Type;
        if (CbAcRepeat.SelectedItem is RepeatModeChoice rc) s.RepeatMode = rc.Mode;
        if (CbAcActivation.SelectedItem is ActivationChoice ac) s.Activation = ac.Mode;
        s.IntervalMs = (int)SliderAcInterval.Value;
        s.JitterEnabled = ChkAcJitter.IsChecked == true;
        s.JitterMs = (int)SliderAcJitter.Value;
        s.AdaptiveLimit = ChkAcAdaptive.IsChecked == true;
        s.RepeatSeconds = ParseIntOr(TxtAcSeconds.Text, s.RepeatSeconds, 1, 86400);
        s.RepeatCount = ParseIntOr(TxtAcCount.Text, s.RepeatCount, 1, 1_000_000);
        s.UseFixedPosition = ChkAcFixedPos.IsChecked == true;
        s.FixedX = ParseIntOr(TxtAcX.Text, s.FixedX, -100000, 100000);
        s.FixedY = ParseIntOr(TxtAcY.Text, s.FixedY, -100000, 100000);
        s.SafetyCountdownEnabled = ChkAcSafety.IsChecked == true;
        s.SafetyCountdownSeconds = ParseIntOr(TxtAcSafetySeconds.Text, s.SafetyCountdownSeconds, 1, 60);
    }

    // Shows the values actually saved, so text a number box couldn't parse doesn't linger after Save.
    private void ShowCommittedAc()
    {
        _loading = true;
        LoadAcIntoControls(_workingAc);
        _loading = false;
    }

    private static int ParseIntOr(string? text, int fallback, int min, int max) =>
        int.TryParse((text ?? "").Trim(), out var v) ? Math.Clamp(v, min, max) : fallback;

    private void MarkAcDirty() { if (!_loading) SetAcDirty(true); }
    private void SetAcDirty(bool v) { _acDirty = v; UpdateDirtyIndicators(); }

    private void UpdateAcConditionalRows()
    {
        var mode = (CbAcRepeat.SelectedItem as RepeatModeChoice)?.Mode ?? RepeatMode.UntilStopped;
        RowAcSeconds.IsVisible = mode == RepeatMode.ForDuration;
        RowAcCount.IsVisible = mode == RepeatMode.ForCount;
        RowAcFixedPos.IsVisible = ChkAcFixedPos.IsChecked == true;
        SliderAcJitter.IsEnabled = ChkAcJitter.IsChecked == true;
        TxtAcSafetySeconds.IsEnabled = ChkAcSafety.IsChecked == true;
        TxtAcInterval.Text = $"{(int)SliderAcInterval.Value} ms";
        TxtAcJitter.Text = $"± {(int)SliderAcJitter.Value} ms";
        // Click type and the fixed point only apply to mouse buttons.
        bool mouse = HotkeyBinding.IsMouseButton(HkAcAction.BindingValue?.VirtualKey ?? 0);
        RowAcClickType.IsEnabled = RowAcFixedToggle.IsEnabled = RowAcFixedPos.IsEnabled = mouse;
    }

    private void AcControl_Changed(object? sender, RoutedEventArgs e)
    {
        UpdateAcConditionalRows();
        MarkAcDirty();
    }

    private void AcSlider_Changed(object? sender, RangeBaseValueChangedEventArgs e)
    {
        if (!_ready) return;
        UpdateAcConditionalRows();
        MarkAcDirty();
    }

    private void AcSetLeftClick_Click(object? sender, RoutedEventArgs e) =>
        HkAcAction.BindingValue = new HotkeyBinding { VirtualKey = VirtualKeys.VK_LBUTTON };

    private bool _picking;

    /// <summary>Pick a point: the next left click anywhere sets X and Y; right-click cancels. The pick click itself is swallowed.</summary>
    private async void AcCapturePos_Click(object? sender, RoutedEventArgs e)
    {
        if (_picking) return;
        _picking = true;
        BtnAcCapturePos.Content = "Click a target…  (right-click to cancel)";
        var point = await Task.Run(X11.PickPoint);
        _picking = false;
        BtnAcCapturePos.Content = "Pick point on screen…";
        if (point is var (x, y))
        {
            TxtAcX.Text = x.ToString();
            TxtAcY.Text = y.ToString();
        }
    }

    private async void CbAcProfile_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressAcCombo || CbAcProfile.SelectedItem is not string name || name == _openAcName) return;
        if (!await EnsureAcSavedOrDiscarded())
        {
            RefreshCombo(CbAcProfile, _config.AutoClickerProfileNames, _openAcName, v => _suppressAcCombo = v);
            return;
        }
        _config.SwitchAutoClickerProfile(name);
        LoadAcProfile(name);
        ReloadServices();
    }

    private async void SaveAc_Click(object? sender, RoutedEventArgs e) => await DoSaveAc();

    private async Task<bool> DoSaveAc()
    {
        CommitAcControls(_workingAc);
        ShowCommittedAc();
        if (await CheckConflicts()) return false;
        _config.SaveAutoClickerProfile(_openAcName, _workingAc, HkAcStartStop.BindingValue ?? new(), makeActive: true);
        ReloadServices();
        SetAcDirty(false);
        return true;
    }

    private async void SaveAsAc_Click(object? sender, RoutedEventArgs e)
    {
        CommitAcControls(_workingAc);
        ShowCommittedAc();
        if (await CheckConflicts()) return;
        if (await Dialogs.SaveAsAsync(this, _config.AutoClickerProfileNames, SuggestName(_openAcName + " copy", _config.AutoClickerProfileNames)) is not string name) return;
        _config.SaveAutoClickerProfile(name, _workingAc, HkAcStartStop.BindingValue ?? new(), makeActive: true);
        LoadAcProfile(StoredName(name, _config.AutoClickerProfileNames));
        RebuildGameProfilesTab();
        ReloadServices();
    }

    private async void NewAc_Click(object? sender, RoutedEventArgs e)
    {
        if (!await EnsureAcSavedOrDiscarded()) return;
        if (await PromptName("New auto-clicker slot", "Name for the new auto-clicker slot:", SuggestName("Auto clicker", _config.AutoClickerProfileNames), _config.AutoClickerProfileExists) is not string name) return;
        _config.CreateAutoClickerProfile(name);
        _config.SwitchAutoClickerProfile(name);
        LoadAcProfile(name);
        RebuildGameProfilesTab();
        ReloadServices();
    }

    private async void RenameAc_Click(object? sender, RoutedEventArgs e)
    {
        var name = await PromptName("Rename auto-clicker slot", "New name:", _openAcName, _config.AutoClickerProfileExists, allowName: _openAcName);
        if (name is null || name == _openAcName || !_config.RenameAutoClickerProfile(_openAcName, name)) return;
        _openAcName = name;
        RefreshCombo(CbAcProfile, _config.AutoClickerProfileNames, _openAcName, v => _suppressAcCombo = v);
        RebuildGameProfilesTab();
    }

    private async void DeleteAc_Click(object? sender, RoutedEventArgs e)
    {
        if (_config.AutoClickerProfileNames.Count <= 1) return;
        if (await Dialogs.AskAsync(this, $"Delete the auto-clicker slot \"{_openAcName}\"? This cannot be undone.", "OK", "Cancel") != 0) return;
        _config.DeleteAutoClickerProfile(_openAcName);
        LoadAcProfile(_config.ActiveAutoClickerProfileName);
        RebuildGameProfilesTab();
        ReloadServices();
    }

    private async Task<bool> EnsureAcSavedOrDiscarded()
    {
        if (!_acDirty) return true;
        return await Dialogs.AskAsync(this, $"Save changes to the auto-clicker slot \"{_openAcName}\" first?", "Yes", "No", "Cancel") switch
        {
            0 => await DoSaveAc(),
            1 => true,
            _ => false,
        };
    }

    // ═════════════════════════ Shared bits ═════════════════════════

    private static HotkeyBinding Clone(HotkeyBinding b) => new() { Modifiers = b.Modifiers, VirtualKey = b.VirtualKey };

    private static void RefreshCombo(ComboBox combo, IReadOnlyList<string> names, string selected, Action<bool> suppress)
    {
        suppress(true);
        combo.ItemsSource = names.ToList();
        combo.SelectedItem = selected;
        suppress(false);
    }

    private void ApplyOpenSettingsHotkey()
    {
        if (_loading) return;
        _config.SetOpenSettingsHotkey(HkOpenSettings.BindingValue ?? new());
        _hotkeys.ReloadBindings();
    }

    protected override async void OnClosing(WindowClosingEventArgs e)
    {
        if (SuppressUnsavedPrompt || _closeConfirmed || !(_zoomDirty || _xhDirty || _acDirty))
        {
            base.OnClosing(e);
            return;
        }
        e.Cancel = true;   // the prompts are async: ask, then close again
        if (await EnsureZoomSavedOrDiscarded() && await EnsureXhSavedOrDiscarded() && await EnsureAcSavedOrDiscarded())
        {
            _closeConfirmed = true;
            Close();
        }
    }

    private static string StoredName(string typed, IReadOnlyList<string> names) =>
        names.FirstOrDefault(n => string.Equals(n, typed, StringComparison.OrdinalIgnoreCase)) ?? typed;

    private static string SuggestName(string baseName, IReadOnlyList<string> existing)
    {
        var taken = existing.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!taken.Contains(baseName)) return baseName;
        for (int i = 2; ; i++)
            if (!taken.Contains($"{baseName} ({i})")) return $"{baseName} ({i})";
    }

    private Task<string?> PromptName(string title, string prompt, string initial, Func<string, bool> exists, string? allowName = null) =>
        Dialogs.PromptNameAsync(this, title, prompt, initial, name =>
        {
            if (string.IsNullOrWhiteSpace(name)) return "Please enter a name.";
            if (!string.Equals(name, allowName, StringComparison.OrdinalIgnoreCase) && exists(name)) return "A slot with that name already exists.";
            return null;
        });

    private async Task<bool> CheckConflicts()
    {
        // Duplicate bindings across everything grabbed together. The auto-clicker action key is what gets pressed, not
        // a hotkey, so it's checked separately below.
        var bindings = new[]
        {
            ("Toggle Zoom", HkToggle.BindingValue),
            ("Zoom In", HkZoomIn.BindingValue),
            ("Zoom Out", HkZoomOut.BindingValue),
            ("Show/hide Crosshair", HkCrosshair.BindingValue),
            ("Hide Crosshair (held)", HkHoldHideCrosshair.BindingValue),
            ("Start/stop Auto-clicker", HkAcStartStop.BindingValue),
            ("Open Settings", HkOpenSettings.BindingValue),
        }.Where(b => b.Item2 is { IsEmpty: false }).ToArray();

        var seen = new HashSet<string>();
        var dupes = bindings.Where(b => !seen.Add($"{b.Item2!.Modifiers}:{b.Item2.VirtualKey}")).Select(b => b.Item1).ToList();
        if (dupes.Count > 0)
        {
            await Dialogs.AskAsync(this, $"The same key is bound to more than one action: {string.Join(", ", dupes)}.\n\nPlease change one before saving.", "OK");
            return true;
        }

        var action = HkAcAction.BindingValue;
        var startStop = HkAcStartStop.BindingValue;
        if (action is { IsEmpty: false } && startStop is { IsEmpty: false } && action.VirtualKey == startStop.VirtualKey)
        {
            await Dialogs.AskAsync(this, "The auto-clicker's repeated key can't be the same as its start/stop key.\n\nPlease change one before saving.", "OK");
            return true;
        }

        // In Hold mode the whole combo stays down for the run, so every click would carry its modifiers.
        if (CbAcActivation.SelectedItem is ActivationChoice { Mode: ActivationMode.Hold } && startStop is { IsEmpty: false, Modifiers: not 0 })
        {
            await Dialogs.AskAsync(this, "In Hold mode the start/stop key can't use Ctrl, Alt, Shift or Super.\n\nPick a plain key or switch to Toggle.", "OK");
            return true;
        }
        return false;
    }

    private void UpdateDirtyIndicators()
    {
        PillZoomUnsaved.IsVisible = DotZoom.IsVisible = _zoomDirty;
        PillXhUnsaved.IsVisible = DotXh.IsVisible = _xhDirty;
        PillAcUnsaved.IsVisible = DotAc.IsVisible = _acDirty;
    }

    private void Startup_Changed(object? sender, RoutedEventArgs e)
    {
        if (_loading) return;
        try { Autostart.SetEnabled(ChkStartup.IsChecked == true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _ = Dialogs.AskAsync(this, $"Could not update the autostart entry:\n{ex.Message}", "OK");
        }
    }

    private void HotkeyWarnings_Changed(object? sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _config.ShowHotkeyWarnings = ChkHotkeyWarnings.IsChecked == true;
        _config.Save();
    }

    // ── Updates (app-wide, applied right away) ──

    private void UpdatePrefs_Changed(object? sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _config.Updates.CheckForUpdates = ChkCheckUpdates.IsChecked == true;
        _config.Updates.InstallAutomatically = ChkAutoInstall.IsChecked == true;
        ChkAutoInstall.IsEnabled = _config.Updates.CheckForUpdates;
        _config.Save();
    }

    private void RenderUpdate(string? status = null)
    {
        var u = _updater.Available;
        TxtUpdateTitle.Text = u is null ? $"Oversight {Updater.CurrentVersion}" : $"Oversight {u.Version} is available";
        TxtUpdateStatus.Text = status ?? (u is not null ? $"You have {Updater.CurrentVersion}."
            : AppProfile.IsIsolated ? "Profile copy: doesn't update."
            : !Updater.CanSelfUpdate ? "Not running from the AppImage: updates install only into the released AppImage."
            : _config.Updates.LastCheck is DateTime t ? $"Up to date. Last checked {(t.Date == DateTime.Today ? t.ToString("t") : t.ToString("MMM d"))}."
            : "Not checked yet.");
        BtnUpdateNotes.IsVisible = u is not null;
        BtnUpdate.Content = u is null ? "Check for updates" : Updater.CanSelfUpdate ? "Install and restart" : "Download";
        BtnUpdate.Classes.Set("accent", u is not null);
    }

    private async void Update_Click(object? sender, RoutedEventArgs e)
    {
        BtnUpdate.IsEnabled = false;
        try
        {
            if (_updater.Available is { } u)
            {
                if (!Updater.CanSelfUpdate) { Shell.Open(u.PageUrl); return; }
                TxtUpdateStatus.Text = "Downloading…";
                if (await _updater.InstallAsync(u) is string error) RenderUpdate(error);
                return;
            }
            TxtUpdateStatus.Text = "Checking…";
            try { await _updater.CheckAsync(); RenderUpdate(); }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or KeyNotFoundException)
            {
                RenderUpdate(ex is HttpRequestException { StatusCode: System.Net.HttpStatusCode.NotFound }
                    ? "No releases have been published yet."
                    : "Couldn't reach GitHub. Check your connection and try again.");
            }
        }
        finally { BtnUpdate.IsEnabled = true; }
    }

    private void UpdateNotes_Click(object? sender, RoutedEventArgs e) => Shell.Open(_updater.Available?.PageUrl ?? Updater.ReleasesPage);

    // ═════════════════════════ Game Profiles ═════════════════════════

    private const string OffChoice = "(off)";

    private void RebuildGameProfilesTab()
    {
        _loadingRules = true;
        var zoomChoices = new List<string> { OffChoice };
        zoomChoices.AddRange(_config.ZoomProfileNames);
        var xhChoices = new List<string> { OffChoice };
        xhChoices.AddRange(_config.CrosshairProfileNames);
        var acChoices = new List<string> { OffChoice };
        acChoices.AddRange(_config.AutoClickerProfileNames);

        CbDefaultZoom.ItemsSource = zoomChoices;
        CbDefaultZoom.SelectedItem = ChoiceFor(_config.DefaultZoomProfile, zoomChoices);
        CbDefaultXh.ItemsSource = xhChoices;
        CbDefaultXh.SelectedItem = ChoiceFor(_config.DefaultCrosshairProfile, xhChoices);
        CbDefaultAc.ItemsSource = acChoices;
        CbDefaultAc.SelectedItem = ChoiceFor(_config.DefaultAutoClickerProfile, acChoices);

        var rows = _config.AppRules.Select(r => new AppRuleRow(r.ProcessName, r.DisplayLabel, zoomChoices, xhChoices, acChoices,
            ChoiceFor(r.ZoomProfile, zoomChoices), ChoiceFor(r.CrosshairProfile, xhChoices), ChoiceFor(r.AutoClickerProfile, acChoices), OnRuleRowChanged)).ToList();
        LbRules.ItemsSource = rows;
        TxtNoRules.IsVisible = rows.Count == 0;
        _loadingRules = false;
    }

    private static string ChoiceFor(string? slotName, List<string> choices) =>
        !string.IsNullOrEmpty(slotName) && choices.Contains(slotName) ? slotName : OffChoice;

    private static string? Slot(object? choice) => choice is string s && s != OffChoice ? s : null;

    private void CbDefault_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (_loadingRules || (sender as ComboBox)?.SelectedItem is null) return;
        if (sender == CbDefaultZoom) _config.SetDefaultZoomProfile(Slot(CbDefaultZoom.SelectedItem));
        else if (sender == CbDefaultXh) _config.SetDefaultCrosshairProfile(Slot(CbDefaultXh.SelectedItem));
        else _config.SetDefaultAutoClickerProfile(Slot(CbDefaultAc.SelectedItem));
        _watcher.Reload();
    }

    private void OnRuleRowChanged(AppRuleRow row)
    {
        if (_loadingRules) return;
        _config.SetAppRule(row.ProcessName, row.DisplayName, Slot(row.SelectedZoom), Slot(row.SelectedCrosshair), Slot(row.SelectedAutoClicker));
        _watcher.Reload();
    }

    private async void AddRunningApp_Click(object? sender, RoutedEventArgs e)
    {
        var apps = await Dialogs.PickRunningAppsAsync(this);
        if (apps.Count == 0) return;
        // New rules default to the open zoom slot, with crosshair and auto-clicker off.
        foreach (var app in apps)
            _config.SetAppRule(app.ProcessName, app.DisplayName, _openZoomName, null, null);
        RebuildGameProfilesTab();
        _watcher.Reload();
    }

    private void RemoveRule_Click(object? sender, RoutedEventArgs e)
    {
        if (LbRules.SelectedItem is not AppRuleRow row) return;
        _config.RemoveAppRule(row.ProcessName);
        RebuildGameProfilesTab();
        _watcher.Reload();
    }

    // ── Choice records ─────────────────────────────────────────────────────────

    private sealed record ActivationChoice(string Display, ActivationMode Mode) { public override string ToString() => Display; }
    private static readonly ActivationChoice[] ActivationChoices = [new("Toggle (press on / off)", ActivationMode.Toggle), new("Hold (while held)", ActivationMode.Hold)];

    private sealed record MouseClickTypeChoice(string Display, MouseClickType Type) { public override string ToString() => Display; }
    private static readonly MouseClickTypeChoice[] ClickTypeChoices = [new("Single click", MouseClickType.Single), new("Double click", MouseClickType.Double)];

    private sealed record RepeatModeChoice(string Display, RepeatMode Mode) { public override string ToString() => Display; }
    private static readonly RepeatModeChoice[] RepeatChoices =
        [new("Until stopped", RepeatMode.UntilStopped), new("A set duration", RepeatMode.ForDuration), new("A set click count", RepeatMode.ForCount)];

    private sealed record ShapeChoice(string Display, CrosshairShape Shape) { public override string ToString() => Display; }
    private static readonly ShapeChoice[] ShapeChoices =
    [
        new("Cross (+)", CrosshairShape.Cross), new("X cross (×)", CrosshairShape.XCross), new("T cross", CrosshairShape.TCross),
        new("Dot", CrosshairShape.Dot), new("Circle", CrosshairShape.Circle), new("Circle + dot", CrosshairShape.CircleDot),
        new("Circle + cross", CrosshairShape.CircleCross),
    ];

    private static readonly ColorChoice[] ColorChoices =
    [
        new("Green", "#00FF40"), new("Red", "#FF3030"), new("Cyan", "#00E5FF"), new("Magenta", "#FF00FF"),
        new("Yellow", "#FFEB3B"), new("Orange", "#FF9100"), new("Pink", "#FF69B4"), new("Lime", "#B0FF00"),
        new("Blue", "#2979FF"), new("Purple", "#B388FF"), new("White", "#FFFFFF"), new("Black", "#000000"),
    ];

    private static string? NormalizeHex(string? text)
    {
        var t = (text ?? "").Trim().TrimStart('#');
        return t.Length == 6 && t.All(Uri.IsHexDigit) ? $"#{t.ToUpperInvariant()}" : null;
    }
}

/// <summary>Colour swatch for the crosshair colour combo.</summary>
public sealed record ColorChoice(string Name, string Hex)
{
    public IBrush Brush => new SolidColorBrush(Color.Parse(Hex));
}

/// <summary>Row of the per-game rules list; the combos write back the chosen slots.</summary>
public sealed class AppRuleRow(string processName, string displayName, IReadOnlyList<string> zoomChoices, IReadOnlyList<string> crosshairChoices,
    IReadOnlyList<string> autoClickerChoices, string zoom, string crosshair, string autoClicker, Action<AppRuleRow> changed) : INotifyPropertyChanged
{
    public string ProcessName { get; } = processName;
    public string DisplayName { get; } = displayName;
    public string Initial => DisplayName.Length > 0 ? char.ToUpperInvariant(DisplayName[0]).ToString() : "?";
    public IReadOnlyList<string> ZoomChoices { get; } = zoomChoices;
    public IReadOnlyList<string> CrosshairChoices { get; } = crosshairChoices;
    public IReadOnlyList<string> AutoClickerChoices { get; } = autoClickerChoices;

    public string SelectedZoom { get => zoom; set => Set(ref zoom, value); }
    public string SelectedCrosshair { get => crosshair; set => Set(ref crosshair, value); }
    public string SelectedAutoClicker { get => autoClicker; set => Set(ref autoClicker, value); }

    private void Set(ref string field, string? value, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (value is null || field == value) return;   // a combo briefly reports null while its items reload
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        changed(this);
    }

    public override string ToString() => DisplayName;
    public event PropertyChangedEventHandler? PropertyChanged;
}
