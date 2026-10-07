using System.ComponentModel;
using System.Windows;
using System.Windows.Interop;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MessageBox = System.Windows.MessageBox;
using Oversight.Models;
using Oversight.Native;
using Oversight.Services;

namespace Oversight.UI;

/// <summary>
/// The Oversight settings window. Zoom and crosshair are two independent documents: each tab
/// edits one open save slot (always the live/active one of its kind) with its own dirty flag
/// and Save/Save As buttons. The Game Profiles tab and the app-wide bits (startup, the Open
/// settings keybind) apply immediately.
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly ConfigStore        _config;
    private readonly HotkeyManager      _hotkeys;
    private readonly ProcessWatcher     _watcher;
    private readonly CrosshairService   _crosshair;
    private readonly AutoClickerService _autoClicker;

    // Zoom document
    private ZoomSettings _workingZoom = new();
    private string       _openZoomName = "";
    private bool _zoomDirty;
    private bool ZoomDirty
    {
        get => _zoomDirty;
        set { _zoomDirty = value; UpdateDirtyIndicators(); }
    }

    // Crosshair document
    private CrosshairSettings _workingXh = new();
    private string            _openXhName = "";
    private bool _xhDirty;
    private bool XhDirty
    {
        get => _xhDirty;
        set { _xhDirty = value; UpdateDirtyIndicators(); }
    }

    // Auto-clicker document
    private AutoClickerSettings _workingAc = new();
    private string              _openAcName = "";
    private bool _acDirty;
    private bool AcDirty
    {
        get => _acDirty;
        set { _acDirty = value; UpdateDirtyIndicators(); }
    }

    private bool _loading;               // suppress dirty-marking while populating controls
    private bool _suppressZoomCombo;     // suppress SelectionChanged while we set the combo
    private bool _suppressXhCombo;
    private bool _suppressAcCombo;

    /// <summary>Set by the tray before closing the window on app shutdown, so we don't prompt mid-quit.</summary>
    public bool SuppressUnsavedPrompt { get; set; }

    public SettingsWindow(ConfigStore config, HotkeyManager hotkeys, ProcessWatcher watcher,
                          CrosshairService crosshair, AutoClickerService autoClicker)
    {
        _config      = config;
        _hotkeys     = hotkeys;
        _watcher     = watcher;
        _crosshair   = crosshair;
        _autoClicker = autoClicker;

        InitializeComponent();

        var version = AppVersionText();
        TabsMain.Tag         = version;   // shown in the sidebar footer
        TxtAboutVersion.Text = version;
        StateChanged += (_, _) => UpdateMaxRestoreButton();

        // Windows 11: title bar matched to the system theme, rounded corners, Mica backdrop.
        DwmHelper.ApplySettingsWindow(this, ThemeManager.IsDarkMode);

        // The window content re-themes itself via DynamicResource brushes; the DWM
        // title bar can't observe those, so re-apply it when the system theme flips.
        ThemeManager.ThemeChanged += OnThemeChanged;
        Closed += (_, _) =>
        {
            ThemeManager.ThemeChanged -= OnThemeChanged;
            _crosshair.SetPreview(null);   // end the live on-screen preview
            if (_pickHook != IntPtr.Zero)  // window closed mid-pick — drop the mouse hook
            {
                NativeMethods.UnhookWindowsHookEx(_pickHook);
                _pickHook = IntPtr.Zero;
                _pickProc = null;
            }
        };

        Loaded += (_, _) => LoadInitial();

        // Pause foreground-driven slot switching while the window is on screen so the user
        // can edit/preview slots without the settings window itself yanking them away; resume
        // when hidden to the tray, minimized, or closed — a minimized settings window must
        // not keep the hotkeys enabled everywhere in defiance of the Game Profiles rules.
        IsVisibleChanged += (_, e) =>
        {
            if ((bool)e.NewValue)
            {
                _watcher.SuspendAutoSwitch();
                PushOrClearLivePreview();
            }
            else
            {
                _crosshair.SetPreview(null);
                _watcher.ResumeAutoSwitch();
            }
        };
        StateChanged += (_, _) =>
        {
            if (!IsVisible) return;
            if (WindowState == WindowState.Minimized)
            {
                _crosshair.SetPreview(null);
                _watcher.ResumeAutoSwitch();
            }
            else
            {
                _watcher.SuspendAutoSwitch();
                PushOrClearLivePreview();
            }
        };

        // Keep the hotkeys live while the window is open so Oversight keeps working — only
        // suspend them for the instant a capture box is recording a new key, otherwise the
        // global hotkey would swallow the keypress. Binding changes mark the right document
        // dirty; the app-wide Open-settings bind applies immediately instead.
        var dpd = DependencyPropertyDescriptor.FromProperty(
            HotkeyCaptureBox.BindingValueProperty, typeof(HotkeyCaptureBox));
        foreach (var box in new[] { HkToggle, HkZoomIn, HkZoomOut, HkCrosshair, HkHoldHideCrosshair,
                                    HkAcAction, HkAcStartStop, HkOpenSettings })
        {
            box.CaptureStarted += _hotkeys.Suspend;
            box.CaptureStopped += _hotkeys.Resume;
        }
        foreach (var box in new[] { HkToggle, HkZoomIn, HkZoomOut })
            dpd.AddValueChanged(box, (_, _) => MarkZoomDirty());
        dpd.AddValueChanged(HkCrosshair, (_, _) => MarkXhDirty());
        dpd.AddValueChanged(HkHoldHideCrosshair, (_, _) => MarkXhDirty());
        dpd.AddValueChanged(HkAcAction, (_, _) => MarkAcDirty());
        dpd.AddValueChanged(HkAcStartStop, (_, _) => MarkAcDirty());
        dpd.AddValueChanged(HkOpenSettings, (_, _) => ApplyOpenSettingsHotkey());
    }

    // ── Initial load ────────────────────────────────────────────────────────────

    private void LoadInitial()
    {
        CbActivation.ItemsSource   = ActivationChoices;
        CbXhShape.ItemsSource      = ShapeChoices;
        CbXhColor.ItemsSource      = ColorChoices;
        CbAcClickType.ItemsSource  = ClickTypeChoices;
        CbAcRepeat.ItemsSource     = RepeatChoices;
        CbAcActivation.ItemsSource = ActivationChoices;

        _loading = true;
        try { ChkStartup.IsChecked = StartupManager.IsEnabled; }
        catch { ChkStartup.IsEnabled = false; }
        HkOpenSettings.BindingValue = Clone(_config.OpenSettingsHotkey);
        _loading = false;

        LoadZoomProfile(_config.ActiveZoomProfileName);
        LoadXhProfile(_config.ActiveCrosshairProfileName);
        LoadAcProfile(_config.ActiveAutoClickerProfileName);
        RebuildGameProfilesTab();
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
        _workingZoom  = _config.GetZoomSettingsCopy(name);

        _loading = true;
        LoadZoomIntoControls(_workingZoom);
        _loading = false;

        ZoomDirty = false;
        RefreshZoomCombo();
        BtnDeleteProfile.IsEnabled = _config.ZoomProfileNames.Count > 1;
        TxtZoomHkNote.Text = $"Saved with the zoom slot (open: “{name}” — use Save on the Zoom tab).";
    }

    private void LoadZoomIntoControls(ZoomSettings s)
    {
        CbActivation.SelectedItem = ActivationChoices.FirstOrDefault(c => c.Mode == s.ToggleZoomMode)
                                    ?? ActivationChoices[0];

        SliderZoom.Value = s.DefaultZoomLevel;
        TxtZoom.Text     = $"{s.DefaultZoomLevel:F2}×";
        SliderStep.Value = s.ZoomStep;
        TxtStep.Text     = $"{s.ZoomStep:F2}";

        ChkZoomIn.IsChecked = s.ZoomInAnimationEnabled;
        SliderAnim.Value    = s.ZoomAnimationMs;
        TxtAnim.Text        = FormatAnim(s.ZoomAnimationMs);

        ChkZoomOut.IsChecked = s.ZoomOutAnimationEnabled;
        SliderAnimOut.Value  = s.ZoomOutAnimationMs;
        TxtAnimOut.Text      = FormatAnim(s.ZoomOutAnimationMs);

        ChkSmoothPan.IsChecked = s.SmoothPanEnabled;
        SliderPan.Value        = s.PanSmoothingMs;
        TxtPan.Text            = FormatAnim(s.PanSmoothingMs);

        HkToggle.BindingValue  = Clone(s.Hotkeys.GetValueOrDefault("ToggleZoom", new()));
        HkZoomIn.BindingValue  = Clone(s.Hotkeys.GetValueOrDefault("ZoomIn",     new()));
        HkZoomOut.BindingValue = Clone(s.Hotkeys.GetValueOrDefault("ZoomOut",    new()));
    }

    private void CommitZoomControls(ZoomSettings s)
    {
        if (CbActivation.SelectedItem is ActivationChoice activation)
            s.ToggleZoomMode = activation.Mode;

        s.DefaultZoomLevel = (float)SliderZoom.Value;
        s.ZoomStep         = (float)SliderStep.Value;

        s.ZoomInAnimationEnabled  = ChkZoomIn.IsChecked == true;
        s.ZoomAnimationMs         = (int)SliderAnim.Value;
        s.ZoomOutAnimationEnabled = ChkZoomOut.IsChecked == true;
        s.ZoomOutAnimationMs      = (int)SliderAnimOut.Value;

        s.SmoothPanEnabled = ChkSmoothPan.IsChecked == true;
        s.PanSmoothingMs   = (int)SliderPan.Value;

        s.Hotkeys["ToggleZoom"] = HkToggle.BindingValue  ?? new();
        s.Hotkeys["ZoomIn"]     = HkZoomIn.BindingValue  ?? new();
        s.Hotkeys["ZoomOut"]    = HkZoomOut.BindingValue ?? new();
    }

    private void MarkZoomDirty()
    {
        if (!_loading) ZoomDirty = true;
    }

    private void RefreshZoomCombo()
    {
        _suppressZoomCombo     = true;
        CbProfile.ItemsSource  = _config.ZoomProfileNames.ToList();
        CbProfile.SelectedItem = _openZoomName;
        _suppressZoomCombo     = false;
    }

    private void CbProfile_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_suppressZoomCombo) return;
        if (CbProfile.SelectedItem is not string name || name == _openZoomName) return;

        if (!EnsureZoomSavedOrDiscarded())
        {
            _suppressZoomCombo     = true;
            CbProfile.SelectedItem = _openZoomName;
            _suppressZoomCombo     = false;
            return;
        }

        _config.SwitchZoomProfile(name);   // open == active
        LoadZoomProfile(name);
        ReloadServices();
    }

    private void SaveProfile_Click(object sender, RoutedEventArgs e) => DoSaveZoom();

    /// <summary>Writes the current zoom edits to the open slot + applies them. False on a binding conflict.</summary>
    private bool DoSaveZoom()
    {
        CommitZoomControls(_workingZoom);
        if (CheckConflicts()) return false;

        _config.SaveZoomProfile(_openZoomName, _workingZoom, makeActive: true);
        ReloadServices();
        ZoomDirty = false;
        return true;
    }

    private void SaveAsProfile_Click(object sender, RoutedEventArgs e)
    {
        CommitZoomControls(_workingZoom);
        if (CheckConflicts()) return;

        var dlg = new ProfileSaveAsDialog(this, _config.ZoomProfileNames,
            SuggestName(_openZoomName + " copy", _config.ZoomProfileNames));
        if (dlg.ShowDialog() != true) return;

        _config.SaveZoomProfile(dlg.EnteredName, _workingZoom, makeActive: true);
        LoadZoomProfile(dlg.EnteredName);
        RebuildGameProfilesTab();
        ReloadServices();
    }

    private void NewProfile_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureZoomSavedOrDiscarded()) return;

        var name = PromptName("New zoom slot", "Name for the new zoom slot:",
            SuggestName("Profile", _config.ZoomProfileNames), _config.ZoomProfileExists);
        if (name is null) return;

        _config.CreateZoomProfile(name);
        _config.SwitchZoomProfile(name);
        LoadZoomProfile(name);
        RebuildGameProfilesTab();
        ReloadServices();
    }

    private void RenameProfile_Click(object sender, RoutedEventArgs e)
    {
        var name = PromptName("Rename zoom slot", "New name:", _openZoomName,
            _config.ZoomProfileExists, allowName: _openZoomName);
        if (name is null || name == _openZoomName) return;

        if (!_config.RenameZoomProfile(_openZoomName, name)) return;
        _openZoomName = name;
        RefreshZoomCombo();
        RebuildGameProfilesTab();
        TxtZoomHkNote.Text = $"Saved with the zoom slot (open: “{name}” — use Save on the Zoom tab).";
    }

    private void DeleteProfile_Click(object sender, RoutedEventArgs e)
    {
        if (_config.ZoomProfileNames.Count <= 1) return;

        var result = MessageBox.Show(this,
            $"Delete the zoom slot \"{_openZoomName}\"? This cannot be undone.",
            "Oversight", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (result != MessageBoxResult.OK) return;

        _config.DeleteZoomProfile(_openZoomName);
        LoadZoomProfile(_config.ActiveZoomProfileName);   // store picked a new active slot
        RebuildGameProfilesTab();
        ReloadServices();
    }

    /// <summary>If the open zoom slot has unsaved edits, asks to save/discard/cancel. False = abort.</summary>
    private bool EnsureZoomSavedOrDiscarded()
    {
        if (!ZoomDirty) return true;

        var choice = MessageBox.Show(this,
            $"Save changes to the zoom slot \"{_openZoomName}\" first?",
            "Oversight", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);

        return choice switch
        {
            MessageBoxResult.Yes => DoSaveZoom(),
            MessageBoxResult.No  => true,
            _                    => false,
        };
    }

    // ═════════════════════════ Crosshair document ═════════════════════════

    private void LoadXhProfile(string name)
    {
        _openXhName = name;
        _workingXh  = _config.GetCrosshairStyleCopy(name);

        _loading = true;
        LoadXhIntoControls(_workingXh);
        HkCrosshair.BindingValue         = _config.GetCrosshairHotkeyCopy(name);
        HkHoldHideCrosshair.BindingValue = _config.GetCrosshairHoldHideHotkeyCopy(name);
        _loading = false;

        XhDirty = false;
        RefreshXhCombo();
        BtnDeleteXh.IsEnabled = _config.CrosshairProfileNames.Count > 1;
        TxtXhHkNote.Text = $"Saved with the crosshair slot (open: “{name}” — use Save on the Crosshair tab).";
        UpdateCrosshairPreview();
    }

    private void LoadXhIntoControls(CrosshairSettings s)
    {
        CbXhShape.SelectedItem  = ShapeChoices.FirstOrDefault(c => c.Shape == s.Shape) ?? ShapeChoices[0];
        SliderXhSize.Value      = Math.Clamp(s.Size, 8, 128);
        SliderXhThickness.Value = Math.Clamp(s.Thickness, 1, 10);
        SliderXhGap.Value       = Math.Clamp(s.Gap, 0, 40);
        SliderXhOpacity.Value   = Math.Clamp(s.Opacity, 10, 100);
        ChkXhCenterDot.IsChecked = s.CenterDot;
        ChkXhOutline.IsChecked   = s.Outline;
        TxtXhHex.Text            = NormalizeHex(s.ColorHex) ?? "#00FF40";
    }

    private void CommitXhControls(CrosshairSettings s)
    {
        if (CbXhShape.SelectedItem is ShapeChoice shape)
            s.Shape = shape.Shape;
        s.Size      = (int)SliderXhSize.Value;
        s.Thickness = (int)SliderXhThickness.Value;
        s.Gap       = (int)SliderXhGap.Value;
        s.Opacity   = (int)SliderXhOpacity.Value;
        s.CenterDot = ChkXhCenterDot.IsChecked == true;
        s.Outline   = ChkXhOutline.IsChecked == true;
        s.ColorHex  = NormalizeHex(TxtXhHex.Text) ?? s.ColorHex;
    }

    private void MarkXhDirty()
    {
        if (!_loading) XhDirty = true;
    }

    private void RefreshXhCombo()
    {
        _suppressXhCombo         = true;
        CbXhProfile.ItemsSource  = _config.CrosshairProfileNames.ToList();
        CbXhProfile.SelectedItem = _openXhName;
        _suppressXhCombo         = false;
    }

    private void CbXhProfile_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_suppressXhCombo) return;
        if (CbXhProfile.SelectedItem is not string name || name == _openXhName) return;

        if (!EnsureXhSavedOrDiscarded())
        {
            _suppressXhCombo         = true;
            CbXhProfile.SelectedItem = _openXhName;
            _suppressXhCombo         = false;
            return;
        }

        _config.SwitchCrosshairProfile(name);   // open == active
        LoadXhProfile(name);
        ReloadServices();
    }

    private void SaveXh_Click(object sender, RoutedEventArgs e) => DoSaveXh();

    /// <summary>Writes the current crosshair edits to the open slot + applies them. False on a binding conflict.</summary>
    private bool DoSaveXh()
    {
        CommitXhControls(_workingXh);
        if (CheckConflicts()) return false;

        _config.SaveCrosshairProfile(_openXhName, _workingXh,
            HkCrosshair.BindingValue ?? new(), HkHoldHideCrosshair.BindingValue ?? new(), makeActive: true);
        ReloadServices();
        XhDirty = false;
        return true;
    }

    private void SaveAsXh_Click(object sender, RoutedEventArgs e)
    {
        CommitXhControls(_workingXh);
        if (CheckConflicts()) return;

        var dlg = new ProfileSaveAsDialog(this, _config.CrosshairProfileNames,
            SuggestName(_openXhName + " copy", _config.CrosshairProfileNames));
        if (dlg.ShowDialog() != true) return;

        _config.SaveCrosshairProfile(dlg.EnteredName, _workingXh,
            HkCrosshair.BindingValue ?? new(), HkHoldHideCrosshair.BindingValue ?? new(), makeActive: true);
        LoadXhProfile(dlg.EnteredName);
        RebuildGameProfilesTab();
        ReloadServices();
    }

    private void NewXh_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureXhSavedOrDiscarded()) return;

        var name = PromptName("New crosshair slot", "Name for the new crosshair slot:",
            SuggestName("Crosshair", _config.CrosshairProfileNames), _config.CrosshairProfileExists);
        if (name is null) return;

        _config.CreateCrosshairProfile(name);
        _config.SwitchCrosshairProfile(name);
        LoadXhProfile(name);
        RebuildGameProfilesTab();
        ReloadServices();
    }

    private void RenameXh_Click(object sender, RoutedEventArgs e)
    {
        var name = PromptName("Rename crosshair slot", "New name:", _openXhName,
            _config.CrosshairProfileExists, allowName: _openXhName);
        if (name is null || name == _openXhName) return;

        if (!_config.RenameCrosshairProfile(_openXhName, name)) return;
        _openXhName = name;
        RefreshXhCombo();
        RebuildGameProfilesTab();
        TxtXhHkNote.Text = $"Saved with the crosshair slot (open: “{name}” — use Save on the Crosshair tab).";
    }

    private void DeleteXh_Click(object sender, RoutedEventArgs e)
    {
        if (_config.CrosshairProfileNames.Count <= 1) return;

        var result = MessageBox.Show(this,
            $"Delete the crosshair slot \"{_openXhName}\"? This cannot be undone.",
            "Oversight", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (result != MessageBoxResult.OK) return;

        _config.DeleteCrosshairProfile(_openXhName);
        LoadXhProfile(_config.ActiveCrosshairProfileName);
        RebuildGameProfilesTab();
        ReloadServices();
    }

    private bool EnsureXhSavedOrDiscarded()
    {
        if (!XhDirty) return true;

        var choice = MessageBox.Show(this,
            $"Save changes to the crosshair slot \"{_openXhName}\" first?",
            "Oversight", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);

        return choice switch
        {
            MessageBoxResult.Yes => DoSaveXh(),
            MessageBoxResult.No  => true,
            _                    => false,
        };
    }

    // ── Crosshair style handlers + preview ───────────────────────────────────

    private bool _syncingColor;   // swatch combo ↔ hex box round-trip guard

    /// <summary>Common tail for every style-control change: dirty + repaint the previews.</summary>
    private void OnCrosshairStyleChanged()
    {
        if (_loading) return;
        MarkXhDirty();
        UpdateCrosshairPreview(pushLive: true);
    }

    private void CbXhShape_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e) =>
        OnCrosshairStyleChanged();

    private void ChkXhCenterDot_Changed(object sender, RoutedEventArgs e) => OnCrosshairStyleChanged();
    private void ChkXhOutline_Changed(object sender, RoutedEventArgs e)   => OnCrosshairStyleChanged();

    private void SliderXhSize_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (TxtXhSize is not null) TxtXhSize.Text = $"{(int)e.NewValue} px";
        OnCrosshairStyleChanged();
    }

    private void SliderXhThickness_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (TxtXhThickness is not null) TxtXhThickness.Text = $"{(int)e.NewValue} px";
        OnCrosshairStyleChanged();
    }

    private void SliderXhGap_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (TxtXhGap is not null) TxtXhGap.Text = $"{(int)e.NewValue} px";
        OnCrosshairStyleChanged();
    }

    private void SliderXhOpacity_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (TxtXhOpacity is not null) TxtXhOpacity.Text = $"{(int)e.NewValue} %";
        OnCrosshairStyleChanged();
    }

    private void CbXhColor_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_loading || _syncingColor) return;
        if (CbXhColor.SelectedItem is not ColorChoice c) return;
        _syncingColor = true;
        TxtXhHex.Text = c.Hex;     // TextChanged still fires → dirty + preview
        _syncingColor = false;
    }

    private void TxtXhHex_Changed(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (TxtXhHex is null || CbXhColor is null) return;

        // Keep the swatch combo in step with the hex box (blank when no swatch matches).
        if (!_syncingColor)
        {
            _syncingColor = true;
            var norm = NormalizeHex(TxtXhHex.Text);
            CbXhColor.SelectedItem = ColorChoices.FirstOrDefault(
                c => string.Equals(c.Hex, norm, StringComparison.OrdinalIgnoreCase));
            _syncingColor = false;
        }

        OnCrosshairStyleChanged();
    }

    /// <summary>
    /// Live on-screen preview policy: shown only while the window is open (not minimized)
    /// with the Crosshair tab front-most; cleared everywhere else so the overlay keeps
    /// obeying the Game Profiles rules.
    /// </summary>
    private void PushOrClearLivePreview()
    {
        if (IsVisible && WindowState != WindowState.Minimized && TabsMain?.SelectedItem == TabXh)
            UpdateCrosshairPreview(pushLive: true);
        else
            _crosshair.SetPreview(null);
    }

    private void TabsMain_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        // SelectionChanged bubbles up from combo boxes inside the tabs — only act on the
        // TabControl's own selection.
        if (!ReferenceEquals(e.OriginalSource, TabsMain)) return;
        PushOrClearLivePreview();
    }

    /// <summary>
    /// Renders the current control values through the real renderer into the preview box, and
    /// optionally pushes them to the on-screen overlay so edits are visible live.
    /// </summary>
    private void UpdateCrosshairPreview(bool pushLive = false)
    {
        if (ImgXhPreview is null || CbXhShape?.SelectedItem is not ShapeChoice) return;

        var style = new CrosshairSettings();
        CommitXhControls(style);

        try
        {
            using var bmp = CrosshairRenderer.Render(style);
            bmp.SetResolution(96, 96);   // 1 bitmap pixel = 1 WPF unit in the preview

            using var ms = new System.IO.MemoryStream();
            bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
            ms.Position = 0;

            var img = new System.Windows.Media.Imaging.BitmapImage();
            img.BeginInit();
            img.CacheOption  = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            img.StreamSource = ms;
            img.EndInit();
            img.Freeze();
            ImgXhPreview.Source = img;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Settings] crosshair preview failed: {ex.Message}");
        }

        if (pushLive) _crosshair.SetPreview(style);
    }

    // ═════════════════════════ Auto-clicker document ═════════════════════════

    private void LoadAcProfile(string name)
    {
        _openAcName = name;
        _workingAc  = _config.GetAutoClickerSettingsCopy(name);

        _loading = true;
        LoadAcIntoControls(_workingAc);
        HkAcStartStop.BindingValue = _config.GetAutoClickerHotkeyCopy(name);
        _loading = false;

        AcDirty = false;
        RefreshAcCombo();
        BtnDeleteAc.IsEnabled = _config.AutoClickerProfileNames.Count > 1;
        TxtAcHkNote.Text = $"Saved with the auto-clicker slot (open: “{name}” — use Save on the Auto Clicker tab).";
    }

    private void LoadAcIntoControls(AutoClickerSettings s)
    {
        HkAcAction.BindingValue = new HotkeyBinding { Modifiers = s.ActionKey.Modifiers, VirtualKey = s.ActionKey.VirtualKey };

        CbAcClickType.SelectedItem  = ClickTypeChoices.FirstOrDefault(c => c.Type == s.ClickType) ?? ClickTypeChoices[0];
        CbAcRepeat.SelectedItem     = RepeatChoices.FirstOrDefault(c => c.Mode == s.RepeatMode) ?? RepeatChoices[0];
        CbAcActivation.SelectedItem = ActivationChoices.FirstOrDefault(c => c.Mode == s.Activation) ?? ActivationChoices[0];

        SliderAcInterval.Value = Math.Clamp(s.IntervalMs, 10, 2000);
        TxtAcInterval.Text     = $"{(int)SliderAcInterval.Value} ms";
        ChkAcJitter.IsChecked  = s.JitterEnabled;
        SliderAcJitter.Value   = Math.Clamp(s.JitterMs, 0, 500);
        TxtAcJitter.Text       = $"± {(int)SliderAcJitter.Value} ms";
        ChkAcAdaptive.IsChecked = s.AdaptiveLimit;

        TxtAcSeconds.Text = s.RepeatSeconds.ToString();
        TxtAcCount.Text   = s.RepeatCount.ToString();

        ChkAcFixedPos.IsChecked = s.UseFixedPosition;
        TxtAcX.Text = s.FixedX.ToString();
        TxtAcY.Text = s.FixedY.ToString();

        ChkAcSafety.IsChecked   = s.SafetyCountdownEnabled;
        TxtAcSafetySeconds.Text = s.SafetyCountdownSeconds.ToString();

        UpdateAcConditionalRows();
    }

    private void CommitAcControls(AutoClickerSettings s)
    {
        if (HkAcAction.BindingValue is { } ak)
            s.ActionKey = new HotkeyBinding { Modifiers = 0, VirtualKey = ak.VirtualKey };

        if (CbAcClickType.SelectedItem is MouseClickTypeChoice ct) s.ClickType  = ct.Type;
        if (CbAcRepeat.SelectedItem    is RepeatModeChoice rc)     s.RepeatMode = rc.Mode;
        if (CbAcActivation.SelectedItem is ActivationChoice ac)    s.Activation = ac.Mode;

        s.IntervalMs    = (int)SliderAcInterval.Value;
        s.JitterEnabled = ChkAcJitter.IsChecked == true;
        s.JitterMs      = (int)SliderAcJitter.Value;
        s.AdaptiveLimit = ChkAcAdaptive.IsChecked == true;

        s.RepeatSeconds = ParseIntOr(TxtAcSeconds.Text, s.RepeatSeconds, 1, 86400);
        s.RepeatCount   = ParseIntOr(TxtAcCount.Text,   s.RepeatCount,   1, 1_000_000);

        s.UseFixedPosition = ChkAcFixedPos.IsChecked == true;
        s.FixedX = ParseIntOr(TxtAcX.Text, s.FixedX, -100000, 100000);
        s.FixedY = ParseIntOr(TxtAcY.Text, s.FixedY, -100000, 100000);

        s.SafetyCountdownEnabled = ChkAcSafety.IsChecked == true;
        s.SafetyCountdownSeconds = ParseIntOr(TxtAcSafetySeconds.Text, s.SafetyCountdownSeconds, 1, 60);
    }

    private static int ParseIntOr(string? text, int fallback, int min, int max) =>
        int.TryParse((text ?? "").Trim(), out var v) ? Math.Clamp(v, min, max) : fallback;

    private void MarkAcDirty()
    {
        if (!_loading) AcDirty = true;
    }

    /// <summary>Shows only the stop-condition / fixed-position fields relevant to the current choices.</summary>
    private void UpdateAcConditionalRows()
    {
        var mode = (CbAcRepeat.SelectedItem as RepeatModeChoice)?.Mode ?? RepeatMode.UntilStopped;
        if (RowAcSeconds is not null)
            RowAcSeconds.Visibility = mode == RepeatMode.ForDuration ? Visibility.Visible : Visibility.Collapsed;
        if (RowAcCount is not null)
            RowAcCount.Visibility = mode == RepeatMode.ForCount ? Visibility.Visible : Visibility.Collapsed;
        if (RowAcFixedPos is not null)
            RowAcFixedPos.Visibility = ChkAcFixedPos.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RefreshAcCombo()
    {
        _suppressAcCombo         = true;
        CbAcProfile.ItemsSource  = _config.AutoClickerProfileNames.ToList();
        CbAcProfile.SelectedItem = _openAcName;
        _suppressAcCombo         = false;
    }

    private void CbAcProfile_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_suppressAcCombo) return;
        if (CbAcProfile.SelectedItem is not string name || name == _openAcName) return;

        if (!EnsureAcSavedOrDiscarded())
        {
            _suppressAcCombo         = true;
            CbAcProfile.SelectedItem = _openAcName;
            _suppressAcCombo         = false;
            return;
        }

        _config.SwitchAutoClickerProfile(name);   // open == active
        LoadAcProfile(name);
        ReloadServices();
    }

    private void SaveAc_Click(object sender, RoutedEventArgs e) => DoSaveAc();

    /// <summary>Writes the current auto-clicker edits to the open slot + applies them. False on a binding conflict.</summary>
    private bool DoSaveAc()
    {
        CommitAcControls(_workingAc);
        if (CheckConflicts()) return false;

        _config.SaveAutoClickerProfile(_openAcName, _workingAc, HkAcStartStop.BindingValue ?? new(), makeActive: true);
        ReloadServices();
        AcDirty = false;
        return true;
    }

    private void SaveAsAc_Click(object sender, RoutedEventArgs e)
    {
        CommitAcControls(_workingAc);
        if (CheckConflicts()) return;

        var dlg = new ProfileSaveAsDialog(this, _config.AutoClickerProfileNames,
            SuggestName(_openAcName + " copy", _config.AutoClickerProfileNames));
        if (dlg.ShowDialog() != true) return;

        _config.SaveAutoClickerProfile(dlg.EnteredName, _workingAc, HkAcStartStop.BindingValue ?? new(), makeActive: true);
        LoadAcProfile(dlg.EnteredName);
        RebuildGameProfilesTab();
        ReloadServices();
    }

    private void NewAc_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureAcSavedOrDiscarded()) return;

        var name = PromptName("New auto-clicker slot", "Name for the new auto-clicker slot:",
            SuggestName("Auto clicker", _config.AutoClickerProfileNames), _config.AutoClickerProfileExists);
        if (name is null) return;

        _config.CreateAutoClickerProfile(name);
        _config.SwitchAutoClickerProfile(name);
        LoadAcProfile(name);
        RebuildGameProfilesTab();
        ReloadServices();
    }

    private void RenameAc_Click(object sender, RoutedEventArgs e)
    {
        var name = PromptName("Rename auto-clicker slot", "New name:", _openAcName,
            _config.AutoClickerProfileExists, allowName: _openAcName);
        if (name is null || name == _openAcName) return;

        if (!_config.RenameAutoClickerProfile(_openAcName, name)) return;
        _openAcName = name;
        RefreshAcCombo();
        RebuildGameProfilesTab();
        TxtAcHkNote.Text = $"Saved with the auto-clicker slot (open: “{name}” — use Save on the Auto Clicker tab).";
    }

    private void DeleteAc_Click(object sender, RoutedEventArgs e)
    {
        if (_config.AutoClickerProfileNames.Count <= 1) return;

        var result = MessageBox.Show(this,
            $"Delete the auto-clicker slot \"{_openAcName}\"? This cannot be undone.",
            "Oversight", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (result != MessageBoxResult.OK) return;

        _config.DeleteAutoClickerProfile(_openAcName);
        LoadAcProfile(_config.ActiveAutoClickerProfileName);
        RebuildGameProfilesTab();
        ReloadServices();
    }

    private bool EnsureAcSavedOrDiscarded()
    {
        if (!AcDirty) return true;

        var choice = MessageBox.Show(this,
            $"Save changes to the auto-clicker slot \"{_openAcName}\" first?",
            "Oversight", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);

        return choice switch
        {
            MessageBoxResult.Yes => DoSaveAc(),
            MessageBoxResult.No  => true,
            _                    => false,
        };
    }

    // ── Auto-clicker control handlers ────────────────────────────────────────

    private void CbAcClickType_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e) => MarkAcDirty();
    private void CbAcActivation_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e) => MarkAcDirty();

    private void CbAcRepeat_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        UpdateAcConditionalRows();
        MarkAcDirty();
    }

    private void ChkAcJitter_Changed(object sender, RoutedEventArgs e)   => MarkAcDirty();
    private void ChkAcSafety_Changed(object sender, RoutedEventArgs e)   => MarkAcDirty();
    private void ChkAcAdaptive_Changed(object sender, RoutedEventArgs e) => MarkAcDirty();

    private void ChkAcFixedPos_Changed(object sender, RoutedEventArgs e)
    {
        UpdateAcConditionalRows();
        MarkAcDirty();
    }

    private void SliderAcInterval_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (TxtAcInterval is not null) TxtAcInterval.Text = $"{(int)e.NewValue} ms";
        MarkAcDirty();
    }

    private void SliderAcJitter_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (TxtAcJitter is not null) TxtAcJitter.Text = $"± {(int)e.NewValue} ms";
        MarkAcDirty();
    }

    private void TxtAcSeconds_Changed(object sender, System.Windows.Controls.TextChangedEventArgs e) => MarkAcDirty();
    private void TxtAcCount_Changed(object sender, System.Windows.Controls.TextChangedEventArgs e)   => MarkAcDirty();
    private void TxtAcPos_Changed(object sender, System.Windows.Controls.TextChangedEventArgs e)     => MarkAcDirty();
    private void TxtAcSafetySeconds_Changed(object sender, System.Windows.Controls.TextChangedEventArgs e) => MarkAcDirty();

    /// <summary>Sets the repeated action to the left mouse button (the capture box can't record it).</summary>
    private void AcSetLeftClick_Click(object sender, RoutedEventArgs e)
    {
        HkAcAction.BindingValue = new HotkeyBinding { Modifiers = 0, VirtualKey = 0x01 };
        // The BindingValue change handler marks the auto-clicker document dirty.
    }

    // ── Screen-point picker (fixed click position) ───────────────────────────

    private IntPtr                            _pickHook;
    private NativeMethods.LowLevelMouseProc?  _pickProc;   // keep referenced while hooked
    private bool                              _picking;

    /// <summary>
    /// Enters "pick a point" mode: after the button click, a low-level mouse hook waits for the
    /// next left-click anywhere on screen and captures that location (right-click cancels). The
    /// pick click is swallowed so it doesn't also land on whatever is underneath.
    /// </summary>
    private void AcCapturePos_Click(object sender, RoutedEventArgs e)
    {
        if (_picking) return;
        _picking = true;
        BtnAcCapturePos.Content = "Click a target…  (right-click to cancel)";

        _pickProc = PickHookProc;
        _pickHook = NativeMethods.SetWindowsHookEx(
            NativeMethods.WH_MOUSE_LL, _pickProc, NativeMethods.GetModuleHandle(null), 0);

        if (_pickHook == IntPtr.Zero)   // hook failed — fall back to the current cursor position
        {
            NativeMethods.GetCursorPos(out var p);
            FinishPick(p.X, p.Y);
        }
    }

    private IntPtr PickHookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode == NativeMethods.HC_ACTION)
        {
            int msg = wParam.ToInt32();
            if (msg == NativeMethods.WM_LBUTTONDOWN)
            {
                NativeMethods.GetCursorPos(out var p);
                Dispatcher.BeginInvoke(() => FinishPick(p.X, p.Y));
                return (IntPtr)1;   // swallow the pick click
            }
            if (msg == NativeMethods.WM_RBUTTONDOWN)
            {
                Dispatcher.BeginInvoke(() => FinishPick(null, null));
                return (IntPtr)1;   // swallow the cancel click
            }
        }
        return NativeMethods.CallNextHookEx(_pickHook, nCode, wParam, lParam);
    }

    /// <summary>Removes the pick hook, restores the button, and writes the point if one was picked.</summary>
    private void FinishPick(int? x, int? y)
    {
        if (_pickHook != IntPtr.Zero)
        {
            NativeMethods.UnhookWindowsHookEx(_pickHook);
            _pickHook = IntPtr.Zero;
        }
        _pickProc = null;
        _picking  = false;
        BtnAcCapturePos.Content = "Pick point on screen…";

        if (x is not null && y is not null)
        {
            TxtAcX.Text = x.Value.ToString();
            TxtAcY.Text = y.Value.ToString();   // TextChanged marks dirty
        }
    }

    // ═════════════════════════ Shared bits ═════════════════════════

    private static HotkeyBinding Clone(HotkeyBinding b) =>
        new() { Modifiers = b.Modifiers, VirtualKey = b.VirtualKey };

    /// <summary>The app-wide Open-settings bind applies immediately — no Save involved.</summary>
    private void ApplyOpenSettingsHotkey()
    {
        if (_loading) return;
        _config.SetOpenSettingsHotkey(HkOpenSettings.BindingValue ?? new());
        _hotkeys.ReloadBindings();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!SuppressUnsavedPrompt &&
            (!EnsureZoomSavedOrDiscarded() || !EnsureXhSavedOrDiscarded() || !EnsureAcSavedOrDiscarded()))
        {
            e.Cancel = true;
            return;
        }
        base.OnClosing(e);
    }

    private static string SuggestName(string baseName, IReadOnlyList<string> existing)
    {
        var taken = existing.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!taken.Contains(baseName)) return baseName;
        for (int i = 2; ; i++)
        {
            var candidate = $"{baseName} ({i})";
            if (!taken.Contains(candidate)) return candidate;
        }
    }

    /// <summary>Shows the name dialog. Returns the trimmed name, or null if cancelled.</summary>
    /// <param name="allowName">A name allowed despite already existing (the slot being renamed).</param>
    private string? PromptName(string title, string prompt, string initial,
        Func<string, bool> exists, string? allowName = null)
    {
        var dlg = new ProfileNameDialog(this, title, prompt, initial, name =>
        {
            if (string.IsNullOrWhiteSpace(name)) return "Please enter a name.";
            if (!string.Equals(name, allowName, StringComparison.OrdinalIgnoreCase) && exists(name))
                return "A slot with that name already exists.";
            return null;
        });

        return dlg.ShowDialog() == true ? dlg.EnteredName : null;
    }

    private bool CheckConflicts()
    {
        // Duplicate bindings across everything currently registered together: the open zoom
        // slot's binds, the open crosshair slot's bind, the auto-clicker start/stop bind, and
        // the app-wide Open-settings bind. (The auto-clicker *action* key is not a global
        // hotkey — it's what gets pressed — so it's excluded here but checked separately below.)
        var bindings = new[]
        {
            ("Toggle Zoom",              HkToggle.BindingValue),
            ("Zoom In",                  HkZoomIn.BindingValue),
            ("Zoom Out",                 HkZoomOut.BindingValue),
            ("Show/hide Crosshair",      HkCrosshair.BindingValue),
            ("Hide Crosshair (held)",    HkHoldHideCrosshair.BindingValue),
            ("Start/stop Auto-clicker",  HkAcStartStop.BindingValue),
            ("Open Settings",            HkOpenSettings.BindingValue),
        }.Where(b => b.Item2 is { IsEmpty: false }).ToArray();

        var seen   = new HashSet<string>();
        var dupes  = new List<string>();
        foreach (var (name, b) in bindings)
        {
            var key = $"{b!.Modifiers}:{b.VirtualKey}";
            if (!seen.Add(key)) dupes.Add(name);
        }

        if (dupes.Count > 0)
        {
            MessageBox.Show(this,
                $"The same key is bound to more than one action: {string.Join(", ", dupes)}.\n\nPlease change one before saving.",
                "Oversight — hotkey conflict", MessageBoxButton.OK, MessageBoxImage.Warning);
            return true;
        }

        // The auto-clicker's repeated key must not be the same as its own start/stop key, or
        // starting it would immediately toggle it back off (Toggle) or fire on the same key (Hold).
        var action = HkAcAction.BindingValue;
        var startStop = HkAcStartStop.BindingValue;
        if (action is { IsEmpty: false } && startStop is { IsEmpty: false } &&
            action.VirtualKey == startStop.VirtualKey)
        {
            MessageBox.Show(this,
                "The auto-clicker's repeated key can't be the same as its start/stop key.\n\nPlease change one before saving.",
                "Oversight — hotkey conflict", MessageBoxButton.OK, MessageBoxImage.Warning);
            return true;
        }

        return false;
    }

    // ── Window chrome ───────────────────────────────────────────────────────────

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void MaxRestore_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    // Swap the maximize / restore glyph to match the window state.
    private void UpdateMaxRestoreButton()
    {
        bool max = WindowState == WindowState.Maximized;
        BtnMaxRestore.Content = max ? "" : "";
        BtnMaxRestore.ToolTip = max ? "Restore" : "Maximize";
        System.Windows.Automation.AutomationProperties.SetName(BtnMaxRestore, max ? "Restore" : "Maximize");
    }

    // Unsaved-changes pills on each slot card + dots beside the sidebar nav items.
    private void UpdateDirtyIndicators()
    {
        // Sliders fire ValueChanged while InitializeComponent is still building the tree,
        // so any of these may not exist yet.
        if (PillZoomUnsaved is null || PillXhUnsaved is null || PillAcUnsaved is null ||
            TabZoom is null || TabXh is null || TabAc is null)
            return;
        PillZoomUnsaved.Visibility = ZoomDirty ? Visibility.Visible : Visibility.Collapsed;
        PillXhUnsaved.Visibility   = XhDirty   ? Visibility.Visible : Visibility.Collapsed;
        PillAcUnsaved.Visibility   = AcDirty   ? Visibility.Visible : Visibility.Collapsed;
        UiProps.SetIsDirty(TabZoom, ZoomDirty);
        UiProps.SetIsDirty(TabXh,   XhDirty);
        UiProps.SetIsDirty(TabAc,   AcDirty);
    }

    private static string AppVersionText()
    {
        var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        return v is null ? "Oversight" : $"Version {v.Major}.{v.Minor}.{v.Build}";
    }

    // Re-paint the DWM title bar to match the new system theme while the window is open.
    private void OnThemeChanged()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        DwmHelper.ApplyTheme(hwnd, ThemeManager.IsDarkMode);
    }

    // ── Zoom-tab handlers (mark dirty; readouts mirror the sliders) ──────────────

    private void CbActivation_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e) => MarkZoomDirty();

    private void ChkZoomIn_Changed(object sender, RoutedEventArgs e)    => MarkZoomDirty();
    private void ChkZoomOut_Changed(object sender, RoutedEventArgs e)   => MarkZoomDirty();
    private void ChkSmoothPan_Changed(object sender, RoutedEventArgs e) => MarkZoomDirty();

    private void SliderAnim_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (TxtAnim is not null) TxtAnim.Text = FormatAnim((int)e.NewValue);
        MarkZoomDirty();
    }

    private void SliderAnimOut_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (TxtAnimOut is not null) TxtAnimOut.Text = FormatAnim((int)e.NewValue);
        MarkZoomDirty();
    }

    private void SliderPan_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (TxtPan is not null) TxtPan.Text = FormatAnim((int)e.NewValue);
        MarkZoomDirty();
    }

    private void SliderZoom_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (TxtZoom is not null) TxtZoom.Text = $"{e.NewValue:F2}×";
        MarkZoomDirty();
    }

    private void SliderStep_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (TxtStep is not null) TxtStep.Text = $"{e.NewValue:F2}";
        MarkZoomDirty();
    }

    // Startup is a global, app-wide toggle (not part of any slot) — apply it right away.
    private void Startup_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        try { StartupManager.SetEnabled(ChkStartup.IsChecked == true); }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not update startup entry:\n{ex.Message}",
                "Oversight", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // ═════════════════════════ Game Profiles tab ═════════════════════════

    private const string OffChoice = "(off)";

    private bool _loadingRules;

    /// <summary>Rebuilds the default selectors + per-game rows from the store's current rules and slots.</summary>
    private void RebuildGameProfilesTab()
    {
        _loadingRules = true;

        var zoomChoices = new List<string> { OffChoice };
        zoomChoices.AddRange(_config.ZoomProfileNames);
        var xhChoices = new List<string> { OffChoice };
        xhChoices.AddRange(_config.CrosshairProfileNames);
        var acChoices = new List<string> { OffChoice };
        acChoices.AddRange(_config.AutoClickerProfileNames);

        CbDefaultZoom.ItemsSource  = zoomChoices;
        CbDefaultZoom.SelectedItem = ChoiceFor(_config.DefaultZoomProfile, zoomChoices);
        CbDefaultXh.ItemsSource    = xhChoices;
        CbDefaultXh.SelectedItem   = ChoiceFor(_config.DefaultCrosshairProfile, xhChoices);
        CbDefaultAc.ItemsSource    = acChoices;
        CbDefaultAc.SelectedItem   = ChoiceFor(_config.DefaultAutoClickerProfile, acChoices);

        var rows = _config.AppRules.Select(r =>
        {
            var row = new AppRuleRow
            {
                ProcessName        = r.ProcessName,
                DisplayName        = r.DisplayLabel,
                ZoomChoices        = zoomChoices,
                CrosshairChoices   = xhChoices,
                AutoClickerChoices = acChoices,
            };
            row.SetInitialSelection(
                ChoiceFor(r.ZoomProfile, zoomChoices),
                ChoiceFor(r.CrosshairProfile, xhChoices),
                ChoiceFor(r.AutoClickerProfile, acChoices));
            row.RuleChanged = OnRuleRowChanged;
            return row;
        }).ToList();

        LbRules.ItemsSource = rows;

        _loadingRules = false;
    }

    // Maps a stored slot name to a combo choice ("(off)" when empty or pointing at a missing slot).
    private static string ChoiceFor(string? slotName, List<string> choices) =>
        !string.IsNullOrEmpty(slotName) && choices.Contains(slotName) ? slotName : OffChoice;

    private void CbDefaultZoom_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_loadingRules) return;
        if (CbDefaultZoom.SelectedItem is not string sel) return;
        _config.SetDefaultZoomProfile(sel == OffChoice ? null : sel);
        _watcher.Reload();
    }

    private void CbDefaultXh_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_loadingRules) return;
        if (CbDefaultXh.SelectedItem is not string sel) return;
        _config.SetDefaultCrosshairProfile(sel == OffChoice ? null : sel);
        _watcher.Reload();
    }

    private void CbDefaultAc_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_loadingRules) return;
        if (CbDefaultAc.SelectedItem is not string sel) return;
        _config.SetDefaultAutoClickerProfile(sel == OffChoice ? null : sel);
        _watcher.Reload();
    }

    private void OnRuleRowChanged(AppRuleRow row)
    {
        if (_loadingRules) return;
        _config.SetAppRule(row.ProcessName, row.DisplayName,
            row.SelectedZoom        == OffChoice ? null : row.SelectedZoom,
            row.SelectedCrosshair   == OffChoice ? null : row.SelectedCrosshair,
            row.SelectedAutoClicker == OffChoice ? null : row.SelectedAutoClicker);
        _watcher.Reload();
    }

    private void AddRunningApp_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new RunningAppsDialog(this);
        if (dlg.ShowDialog() != true) return;

        // New rules default to the open zoom slot, with crosshair and auto-clicker off —
        // a sensible (and safe) start; the user opts the auto-clicker in per game.
        foreach (var app in dlg.SelectedApps)
            _config.SetAppRule(app.ProcessName, app.DisplayName, _openZoomName, null, null);

        RebuildGameProfilesTab();
        _watcher.Reload();
    }

    private void AddSteam_Click(object sender, RoutedEventArgs e) { /* Steam library picker — planned */ }

    private void RemoveRule_Click(object sender, RoutedEventArgs e)
    {
        if (LbRules.SelectedItem is AppRuleRow row)
        {
            _config.RemoveAppRule(row.ProcessName);
            RebuildGameProfilesTab();
            _watcher.Reload();
        }
    }

    /// <summary>Row view-model for the per-game rules list; the combos write back the chosen slots.</summary>
    private sealed class AppRuleRow : INotifyPropertyChanged
    {
        public string ProcessName { get; init; } = string.Empty;
        public string DisplayName { get; init; } = string.Empty;
        public IReadOnlyList<string> ZoomChoices        { get; init; } = [];
        public IReadOnlyList<string> CrosshairChoices   { get; init; } = [];
        public IReadOnlyList<string> AutoClickerChoices { get; init; } = [];

        private string _selectedZoom = string.Empty;
        public string SelectedZoom
        {
            get => _selectedZoom;
            set
            {
                if (_selectedZoom == value) return;
                _selectedZoom = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedZoom)));
                RuleChanged?.Invoke(this);
            }
        }

        private string _selectedCrosshair = string.Empty;
        public string SelectedCrosshair
        {
            get => _selectedCrosshair;
            set
            {
                if (_selectedCrosshair == value) return;
                _selectedCrosshair = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedCrosshair)));
                RuleChanged?.Invoke(this);
            }
        }

        private string _selectedAutoClicker = string.Empty;
        public string SelectedAutoClicker
        {
            get => _selectedAutoClicker;
            set
            {
                if (_selectedAutoClicker == value) return;
                _selectedAutoClicker = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedAutoClicker)));
                RuleChanged?.Invoke(this);
            }
        }

        /// <summary>Callback fired when the user changes a slot (not during initial population).</summary>
        public Action<AppRuleRow>? RuleChanged;

        /// <summary>Sets the initial selections without firing <see cref="RuleChanged"/>.</summary>
        public void SetInitialSelection(string zoom, string crosshair, string autoClicker)
        {
            _selectedZoom        = zoom;
            _selectedCrosshair   = crosshair;
            _selectedAutoClicker = autoClicker;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedZoom)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedCrosshair)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedAutoClicker)));
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    // ── Choice records ─────────────────────────────────────────────────────────

    // Toggle vs hold for the zoom key. The settings ComboBox uses a custom control template
    // whose selection box falls back to ToString(), so the record overrides it.
    private sealed record ActivationChoice(string Display, ActivationMode Mode)
    {
        public override string ToString() => Display;
    }

    private static readonly ActivationChoice[] ActivationChoices =
    {
        new("Toggle (press on / off)",   ActivationMode.Toggle),
        new("Hold (while held)",         ActivationMode.Hold),
    };

    private sealed record MouseClickTypeChoice(string Display, MouseClickType Type)
    {
        public override string ToString() => Display;
    }

    private static readonly MouseClickTypeChoice[] ClickTypeChoices =
    {
        new("Single click", MouseClickType.Single),
        new("Double click", MouseClickType.Double),
    };

    private sealed record RepeatModeChoice(string Display, RepeatMode Mode)
    {
        public override string ToString() => Display;
    }

    private static readonly RepeatModeChoice[] RepeatChoices =
    {
        new("Until stopped",     RepeatMode.UntilStopped),
        new("A set duration",    RepeatMode.ForDuration),
        new("A set click count", RepeatMode.ForCount),
    };

    private sealed record ShapeChoice(string Display, CrosshairShape Shape)
    {
        public override string ToString() => Display;
    }

    private static readonly ShapeChoice[] ShapeChoices =
    {
        new("Cross (+)",      CrosshairShape.Cross),
        new("X cross (×)",    CrosshairShape.XCross),
        new("T cross",        CrosshairShape.TCross),
        new("Dot",            CrosshairShape.Dot),
        new("Circle",         CrosshairShape.Circle),
        new("Circle + dot",   CrosshairShape.CircleDot),
        new("Circle + cross", CrosshairShape.CircleCross),
    };

    /// <summary>Colour swatch for the crosshair colour combo (Brush feeds the item template).</summary>
    public sealed record ColorChoice(string Name, string Hex)
    {
        public System.Windows.Media.Brush Brush
        {
            get
            {
                var b = new System.Windows.Media.SolidColorBrush(
                    (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(Hex));
                b.Freeze();
                return b;
            }
        }
    }

    private static readonly ColorChoice[] ColorChoices =
    {
        new("Green",   "#00FF40"),
        new("Red",     "#FF3030"),
        new("Cyan",    "#00E5FF"),
        new("Magenta", "#FF00FF"),
        new("Yellow",  "#FFEB3B"),
        new("Orange",  "#FF9100"),
        new("Pink",    "#FF69B4"),
        new("Lime",    "#B0FF00"),
        new("Blue",    "#2979FF"),
        new("Purple",  "#B388FF"),
        new("White",   "#FFFFFF"),
        new("Black",   "#000000"),
    };

    /// <summary>Returns "#RRGGBB" (upper-case), or null if the text isn't a valid colour.</summary>
    private static string? NormalizeHex(string? text)
    {
        var t = (text ?? "").Trim().TrimStart('#');
        return t.Length == 6 && t.All(Uri.IsHexDigit) ? $"#{t.ToUpperInvariant()}" : null;
    }

    private static string FormatAnim(int ms) => ms <= 0 ? "Off" : $"{ms} ms";
}
