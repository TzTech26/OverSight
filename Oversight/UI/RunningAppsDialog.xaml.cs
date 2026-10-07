using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Input;
using Oversight.Native;
using Oversight.Services;

namespace Oversight.UI;

/// <summary>
/// Lets the user pick from the apps that currently have a visible top-level window. Returns the
/// chosen apps so the caller can map each to a save slot. (A Steam-library picker is planned to
/// sit beside this.)
/// </summary>
public partial class RunningAppsDialog : Window
{
    public sealed record RunningApp(string ProcessName, string DisplayName);

    private static readonly string OwnProcess = Process.GetCurrentProcess().ProcessName;

    /// <summary>The apps the user chose. Empty if cancelled.</summary>
    public List<RunningApp> SelectedApps { get; private set; } = [];

    public RunningAppsDialog(Window owner)
    {
        InitializeComponent();
        Owner = owner;
        DwmHelper.ApplySettingsWindow(this, ThemeManager.IsDarkMode);
        Loaded += (_, _) => Populate();
    }

    private void Populate()
    {
        LbApps.ItemsSource = EnumerateApps();
    }

    private static List<RunningApp> EnumerateApps()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<RunningApp>();

        NativeMethods.EnumWindows((hwnd, _) =>
        {
            if (!NativeMethods.IsWindowVisible(hwnd)) return true;

            int len = NativeMethods.GetWindowTextLength(hwnd);
            if (len == 0) return true;   // no title → not a user-facing window

            long ex = (long)NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE);
            if ((ex & NativeMethods.WS_EX_TOOLWINDOW) != 0) return true;   // skip tool windows

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

                list.Add(new RunningApp(name, string.IsNullOrEmpty(title) ? name : title));
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
        if (LbApps.SelectedItem is RunningApp) Accept();
    }

    private void Add_Click(object sender, RoutedEventArgs e) => Accept();

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Accept()
    {
        SelectedApps = LbApps.SelectedItems.Cast<RunningApp>().ToList();
        if (SelectedApps.Count == 0) return;
        DialogResult = true;
    }
}
