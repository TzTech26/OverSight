using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Oversight.Native;
using Oversight.Services;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace Oversight.UI;

/// <summary>
/// "Save As" prompt: pick an existing profile from the list to overwrite it, or type a new
/// name to create one. The textbox is the source of truth — selecting a list row just fills it.
/// </summary>
public partial class ProfileSaveAsDialog : Window
{
    private readonly HashSet<string> _existing;

    /// <summary>The chosen profile name (existing = overwrite, new = create).</summary>
    public string EnteredName { get; private set; } = string.Empty;

    public ProfileSaveAsDialog(Window owner, IEnumerable<string> existingProfiles, string suggestedNew)
    {
        InitializeComponent();

        Owner = owner;

        var names = existingProfiles.ToList();
        _existing = names.ToHashSet(StringComparer.OrdinalIgnoreCase);

        LbProfiles.ItemsSource = names;
        TxtName.Text           = suggestedNew;

        DwmHelper.ApplySettingsWindow(this, ThemeManager.IsDarkMode);

        Loaded += (_, _) =>
        {
            TxtName.SelectAll();
            TxtName.Focus();
            UpdateHint();
        };
    }

    private void LbProfiles_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LbProfiles.SelectedItem is string name)
            TxtName.Text = name;   // fires TextChanged → UpdateHint
    }

    private void LbProfiles_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (LbProfiles.SelectedItem is string) TryAccept();
    }

    private void TxtName_TextChanged(object sender, TextChangedEventArgs e) => UpdateHint();

    private void TxtName_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Return) TryAccept();
    }

    private void UpdateHint()
    {
        var name = TxtName.Text.Trim();
        if (string.IsNullOrEmpty(name))
        {
            TxtHint.Text = string.Empty;
            BtnSave.IsEnabled = false;
            return;
        }

        BtnSave.IsEnabled = true;
        TxtHint.Text = _existing.Contains(name)
            ? $"Overwrites the existing slot “{name}”."
            : $"Creates a new slot “{name}”.";
    }

    private void Ok_Click(object sender, RoutedEventArgs e) => TryAccept();

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void TryAccept()
    {
        var name = TxtName.Text.Trim();
        if (string.IsNullOrEmpty(name)) return;

        EnteredName  = name;
        DialogResult = true;
    }
}
