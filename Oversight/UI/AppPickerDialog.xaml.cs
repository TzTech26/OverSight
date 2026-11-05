using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Oversight.Native;
using Oversight.Services;

namespace Oversight.UI;

/// <summary>
/// Searchable list of apps to add to Game Profiles: the apps with a window open, the installed Steam games,
/// or one game's candidate executables. The list loads off the UI thread.
/// </summary>
public partial class AppPickerDialog : Window
{
    /// <summary>A row: <see cref="Detail"/> is the grey second line; <see cref="Game"/> is set for Steam games.</summary>
    public sealed record PickerItem(string ProcessName, string DisplayName, string Detail, string? ExePath, ImageSource? Icon, SteamGame? Game = null)
    {
        public override string ToString() => DisplayName;   // list item accessible name
    }

    private readonly Func<List<PickerItem>> _load;
    private readonly string _emptyText;
    private ICollectionView? _view;

    /// <summary>The chosen rows. Empty if cancelled.</summary>
    public List<PickerItem> SelectedApps { get; private set; } = [];

    public AppPickerDialog(Window owner, string title, string heading, string emptyText, Func<List<PickerItem>> load, bool multiSelect = true)
    {
        InitializeComponent();
        Owner = owner;
        Title = title;
        TxtHeading.Text = heading;
        _emptyText = emptyText;
        _load = load;
        if (!multiSelect)
        {
            LbApps.SelectionMode = System.Windows.Controls.SelectionMode.Single;
            TxtHint.Visibility = Visibility.Collapsed;
            BtnRefresh.Visibility = Visibility.Collapsed;
        }
        DwmHelper.ApplySettingsWindow(this, ThemeManager.IsDarkMode);
        Loaded += (_, _) => { TxtSearch.Focus(); Populate(); };
    }

    /// <summary>Picker over the apps that have a visible top-level window.</summary>
    public static AppPickerDialog RunningApps(Window owner) => new(owner, "Add running app", "Pick a game",
        "No apps with an open window were found. Start your game, then click Refresh.", EnumerateRunningApps);

    private async void Populate()
    {
        UiProps.SetEmptyText(LbApps, null);   // no "nothing found" while loading
        LbApps.ItemsSource = null;
        var items = await Task.Run(_load);
        _view = CollectionViewSource.GetDefaultView(items);
        _view.Filter = Matches;
        LbApps.ItemsSource = _view;
        UiProps.SetEmptyText(LbApps, _emptyText);
        if (LbApps.SelectionMode == System.Windows.Controls.SelectionMode.Single && items.Count > 0) LbApps.SelectedIndex = 0;
    }

    private bool Matches(object o)
    {
        string q = TxtSearch.Text.Trim();
        return q.Length == 0 || o is PickerItem p &&
            (p.DisplayName.Contains(q, StringComparison.OrdinalIgnoreCase) || p.ProcessName.Contains(q, StringComparison.OrdinalIgnoreCase));
    }

    private void TxtSearch_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) => _view?.Refresh();

    private static readonly string OwnProcess = Process.GetCurrentProcess().ProcessName;

    private static List<PickerItem> EnumerateRunningApps()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<PickerItem>();

        NativeMethods.EnumWindows((hwnd, _) =>
        {
            if (!NativeMethods.IsWindowVisible(hwnd)) return true;

            int len = NativeMethods.GetWindowTextLength(hwnd);
            if (len == 0) return true;   // no title → not a user-facing window

            long ex = (long)NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE);
            if ((ex & NativeMethods.WS_EX_TOOLWINDOW) != 0) return true;   // skip tool windows

            // "Visible" but cloaked: background UWP hosts like TextInputHost and ApplicationFrameHost.
            if (NativeMethods.DwmGetWindowAttribute(hwnd, NativeMethods.DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0
                && cloaked != 0) return true;

            NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == 0) return true;

            try
            {
                using var proc = Process.GetProcessById((int)pid);
                var name = proc.ProcessName;
                if (string.Equals(name, OwnProcess, StringComparison.OrdinalIgnoreCase)) return true;
                if (!seen.Add(name)) return true;   // one row per process

                var sb = new StringBuilder(len + 1);
                NativeMethods.GetWindowText(hwnd, sb, sb.Capacity);
                var title = sb.ToString().Trim();
                var exe = AppIcons.ExeOf(proc);

                list.Add(new PickerItem(name, string.IsNullOrEmpty(title) ? name : title, name, exe, AppIcons.For(exe)));
            }
            catch { /* process exited mid-enumeration — skip */ }

            return true;
        }, IntPtr.Zero);

        list.Sort((a, b) => string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase));
        return list;
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => Populate();

    private void LbApps_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (LbApps.SelectedItem is PickerItem) Accept();
    }

    private void Add_Click(object sender, RoutedEventArgs e) => Accept();

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Accept()
    {
        SelectedApps = LbApps.SelectedItems.Cast<PickerItem>().ToList();
        if (SelectedApps.Count == 0) return;
        DialogResult = true;
    }
}

/// <summary>App icons from an exe, or from an image file such as Steam's cached icon. Cached for the session; null when there is none.</summary>
public static class AppIcons
{
    private static readonly ConcurrentDictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);

    private static readonly Lazy<string?> SteamRoot = new(SteamLibrary.FindRoot);

    public static ImageSource? For(string? path) => string.IsNullOrEmpty(path) ? null : Cache.GetOrAdd(path, Load);

    /// <summary>Steam's cached icon for an app id.</summary>
    public static ImageSource? ForSteamApp(string? appId) =>
        appId is null || SteamRoot.Value is not { } root ? null : For(SteamLibrary.IconFor(root, appId));

    /// <summary>The exe behind a process, or null when Windows won't say (elevated or protected processes).</summary>
    public static string? ExeOf(Process p)
    {
        try { return p.MainModule?.FileName; }
        catch { return null; }
    }

    private static ImageSource? Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            ImageSource src;
            if (path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                using var icon = System.Drawing.Icon.ExtractAssociatedIcon(path);
                if (icon is null) return null;
                src = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            }
            else
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.DecodePixelWidth = 64;
                bmp.UriSource = new Uri(path);
                bmp.EndInit();
                src = bmp;
            }
            src.Freeze();   // loaded off the UI thread
            return src;
        }
        catch { return null; }
    }
}
