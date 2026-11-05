using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Oversight.Linux.Platform;
using Oversight.Models;

namespace Oversight.Linux.Ui;

/// <summary>One row in a settings card: title and optional description on the left, a control on the right (template in App.axaml).</summary>
public sealed class SettingRow : ContentControl
{
    public static readonly StyledProperty<string?> HeaderProperty = AvaloniaProperty.Register<SettingRow, string?>(nameof(Header));
    public static readonly StyledProperty<string?> DescriptionProperty = AvaloniaProperty.Register<SettingRow, string?>(nameof(Description));

    public string? Header { get => GetValue(HeaderProperty); set => SetValue(HeaderProperty, value); }
    public string? Description { get => GetValue(DescriptionProperty); set => SetValue(DescriptionProperty, value); }
}

/// <summary>
/// A button that records a key combo when clicked: the next non-modifier key (with the modifiers held) becomes the
/// binding. Escape clears it. With <see cref="AllowMouseButtons"/> it also records right, middle, Mouse 4 and Mouse 5.
/// </summary>
public sealed class HotkeyBox : Button
{
    protected override Type StyleKeyOverride => typeof(Button);

    private HotkeyBinding? _value;
    private bool _capturing;

    public bool AllowMouseButtons { get; set; }
    public event Action? CaptureStarted;
    public event Action? CaptureStopped;
    public event EventHandler? BindingValueChanged;

    public HotkeyBox()
    {
        MinWidth = 180;
        HorizontalContentAlignment = HorizontalAlignment.Center;
        Content = "(none)";
        LostFocus += (_, _) => CancelCapture();
        DetachedFromVisualTree += (_, _) => CancelCapture();
    }

    public HotkeyBinding? BindingValue
    {
        get => _value;
        set
        {
            _value = value;
            if (!_capturing) Content = value?.DisplayName ?? "(none)";
            BindingValueChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    protected override void OnClick()
    {
        if (_capturing) return;
        _capturing = true;
        Content = AllowMouseButtons ? "Press key / mouse button..." : "Press a key...";
        Focus();
        CaptureStarted?.Invoke();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        var p = e.GetCurrentPoint(this).Properties;
        uint vk = !_capturing || !AllowMouseButtons ? 0
            : p.IsRightButtonPressed ? VirtualKeys.VK_RBUTTON
            : p.IsMiddleButtonPressed ? VirtualKeys.VK_MBUTTON
            : p.IsXButton1Pressed ? VirtualKeys.VK_XBUTTON1
            : p.IsXButton2Pressed ? VirtualKeys.VK_XBUTTON2 : 0;
        if (vk != 0)
        {
            StopCapture();
            BindingValue = new HotkeyBinding { VirtualKey = vk };
            e.Handled = true;
            return;
        }
        base.OnPointerPressed(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (!_capturing) { base.OnKeyDown(e); return; }
        e.Handled = true;
        if (e.Key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
            return;   // wait for a real key

        StopCapture();
        if (e.Key == Key.Escape)
        {
            BindingValue = new HotkeyBinding();
            return;
        }
        uint vk = KeyMap.FromAvalonia(e.Key);
        if (vk == 0)
        {
            Content = _value?.DisplayName ?? "(none)";
            return;
        }
        uint mods = (e.KeyModifiers.HasFlag(KeyModifiers.Control) ? VirtualKeys.MOD_CONTROL : 0) |
                    (e.KeyModifiers.HasFlag(KeyModifiers.Alt) ? VirtualKeys.MOD_ALT : 0) |
                    (e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? VirtualKeys.MOD_SHIFT : 0) |
                    (e.KeyModifiers.HasFlag(KeyModifiers.Meta) ? VirtualKeys.MOD_WIN : 0);
        BindingValue = new HotkeyBinding { Modifiers = mods, VirtualKey = vk };
    }

    private void StopCapture()
    {
        if (!_capturing) return;
        _capturing = false;
        CaptureStopped?.Invoke();
    }

    private void CancelCapture()
    {
        if (!_capturing) return;
        StopCapture();
        Content = _value?.DisplayName ?? "(none)";
    }
}

/// <summary>Small modal prompts in the app's style: messages with buttons, a name prompt, Save as, and the running-apps picker.</summary>
public static class Dialogs
{
    private static Window Make(Window owner, string title, Control content, double width = 420) => new()
    {
        Title = title,
        Width = width,
        SizeToContent = SizeToContent.Height,
        CanResize = false,
        WindowStartupLocation = WindowStartupLocation.CenterOwner,
        ShowInTaskbar = false,
        Icon = owner.Icon,
        Background = owner.Background,
        Content = new Border { Padding = new Thickness(20), Child = content },
    };

    private static StackPanel Buttons(params Button[] buttons)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Margin = new Thickness(0, 18, 0, 0) };
        row.Children.AddRange(buttons);
        return row;
    }

    /// <summary>Shows <paramref name="text"/> with the given buttons; returns the index clicked, or -1 when closed.</summary>
    public static async Task<int> AskAsync(Window owner, string text, params string[] buttons)
    {
        int result = -1;
        Window? w = null;
        var bs = buttons.Select((label, i) =>
        {
            var b = new Button { Content = label, MinWidth = 88, IsDefault = i == 0, IsCancel = i == buttons.Length - 1 && buttons.Length > 1 };
            if (i == 0) b.Classes.Add("accent");
            b.Click += (_, _) => { result = i; w!.Close(); };
            return b;
        }).ToArray();
        w = Make(owner, "Oversight", new StackPanel { Children = { new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap }, Buttons(bs) } });
        await w.ShowDialog(owner);
        return result;
    }

    /// <summary>Asks for a slot name; <paramref name="validate"/> returns an error or null. Returns the trimmed name, or null.</summary>
    public static async Task<string?> PromptNameAsync(Window owner, string title, string prompt, string initial, Func<string, string?> validate)
    {
        string? result = null;
        var box = new TextBox { Text = initial, Margin = new Thickness(0, 8, 0, 0) };
        var error = new TextBlock { IsVisible = false, Margin = new Thickness(0, 6, 0, 0), Foreground = Brushes.IndianRed, TextWrapping = TextWrapping.Wrap };
        var ok = new Button { Content = "OK", MinWidth = 88, IsDefault = true, Classes = { "accent" } };
        var cancel = new Button { Content = "Cancel", MinWidth = 88, IsCancel = true };
        var w = Make(owner, title, new StackPanel { Children = { new TextBlock { Text = prompt }, box, error, Buttons(ok, cancel) } });
        ok.Click += (_, _) =>
        {
            string name = (box.Text ?? "").Trim();
            if (validate(name) is string problem) { error.Text = problem; error.IsVisible = true; return; }
            result = name;
            w.Close();
        };
        cancel.Click += (_, _) => w.Close();
        w.Opened += (_, _) => { box.SelectAll(); box.Focus(); };
        await w.ShowDialog(owner);
        return result;
    }

    /// <summary>"Save as": pick an existing slot to overwrite it or type a new name. Returns the name, or null.</summary>
    public static async Task<string?> SaveAsAsync(Window owner, IReadOnlyList<string> existing, string suggested)
    {
        string? result = null;
        var taken = existing.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var list = new ListBox { ItemsSource = existing, Height = 160 };
        var box = new TextBox { Text = suggested, Margin = new Thickness(0, 10, 0, 0) };
        var hint = new TextBlock { Margin = new Thickness(0, 6, 0, 0), Classes = { "caption" } };
        var save = new Button { Content = "Save", MinWidth = 88, IsDefault = true, Classes = { "accent" } };
        var cancel = new Button { Content = "Cancel", MinWidth = 88, IsCancel = true };
        var w = Make(owner, "Save as", new StackPanel { Children = { list, box, hint, Buttons(save, cancel) } });
        void UpdateHint()
        {
            string name = (box.Text ?? "").Trim();
            save.IsEnabled = name.Length > 0;
            hint.Text = name.Length == 0 ? "" : taken.Contains(name) ? $"Overwrites the existing slot “{name}”." : $"Creates a new slot “{name}”.";
        }
        void Accept()
        {
            string name = (box.Text ?? "").Trim();
            if (name.Length == 0) return;
            result = name;
            w.Close();
        }
        list.SelectionChanged += (_, _) => { if (list.SelectedItem is string s) box.Text = s; };
        list.DoubleTapped += (_, _) => { if (list.SelectedItem is string) Accept(); };
        box.TextChanged += (_, _) => UpdateHint();
        save.Click += (_, _) => Accept();
        cancel.Click += (_, _) => w.Close();
        w.Opened += (_, _) => { UpdateHint(); box.SelectAll(); box.Focus(); };
        await w.ShowDialog(owner);
        return result;
    }

    public sealed record RunningApp(string ProcessName, string DisplayName)
    {
        public override string ToString() => DisplayName;
    }

    /// <summary>Lets the user pick from apps with a window open. Returns the chosen apps (empty if cancelled).</summary>
    public static async Task<List<RunningApp>> PickRunningAppsAsync(Window owner)
    {
        var result = new List<RunningApp>();
        var list = new ListBox { SelectionMode = SelectionMode.Multiple | SelectionMode.Toggle, Height = 300, ItemsSource = X11Apps.Running() };
        var add = new Button { Content = "Add", MinWidth = 88, IsDefault = true, Classes = { "accent" } };
        var refresh = new Button { Content = "Refresh" };
        var cancel = new Button { Content = "Cancel", MinWidth = 88, IsCancel = true };
        var w = Make(owner, "Add running app", new StackPanel { Children = { list, Buttons(refresh, add, cancel) } }, 460);
        void Accept()
        {
            result = list.SelectedItems?.OfType<RunningApp>().ToList() ?? [];
            if (result.Count > 0) w.Close();
        }
        add.Click += (_, _) => Accept();
        list.DoubleTapped += (_, _) => Accept();
        refresh.Click += (_, _) => list.ItemsSource = X11Apps.Running();
        cancel.Click += (_, _) => { result = []; w.Close(); };
        await w.ShowDialog(owner);
        return result;
    }
}

/// <summary>The apps with a window open, from the window manager's client list.</summary>
public static class X11Apps
{
    public static List<Dialogs.RunningApp> Running()
    {
        var list = new List<Dialogs.RunningApp>();
        nint d = X11.Display;
        if (d == 0) return list;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (nint w in X11.Longs(d, X11.XDefaultRootWindow(d), "_NET_CLIENT_LIST", X11.XA_WINDOW))
        {
            int pid = X11.WindowPid(d, w);
            if (pid <= 0 || pid == Environment.ProcessId || X11.ProcessName(pid) is not { } name || !seen.Add(name)) continue;
            string title = WindowTitle(d, w);
            list.Add(new Dialogs.RunningApp(name, title.Length > 0 ? title : name));
        }
        list.Sort((a, b) => string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase));
        return list;
    }

    private static unsafe string WindowTitle(nint d, nint w)
    {
        nint utf8 = X11.Atom(d, "UTF8_STRING");
        if (X11.XGetWindowProperty(d, w, X11.Atom(d, "_NET_WM_NAME"), 0, 1024, 0, utf8, out _, out int format, out nuint n, out _, out nint data) != 0 || data == 0)
            return "";
        try { return format == 8 ? System.Text.Encoding.UTF8.GetString((byte*)data, (int)n).Trim() : ""; }
        finally { X11.XFree(data); }
    }
}
