using System.Windows;
using System.Windows.Input;
using Oversight.Native;
using Oversight.Services;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace Oversight.UI;

/// <summary>
/// Small modal prompt for a save-slot name, used for New / Save As / Rename. The caller
/// supplies a validator that returns an error message (or null when the name is acceptable),
/// so duplicate / empty names are rejected inline without closing the dialog.
/// </summary>
public partial class ProfileNameDialog : Window
{
    private readonly Func<string, string?> _validate;

    public string EnteredName { get; private set; } = string.Empty;

    public ProfileNameDialog(Window owner, string title, string prompt,
                             string initialValue, Func<string, string?> validate)
    {
        InitializeComponent();

        Owner       = owner;
        Title       = title;
        TxtPrompt.Text = prompt;
        TxtName.Text   = initialValue;
        _validate   = validate;

        DwmHelper.ApplySettingsWindow(this, ThemeManager.IsDarkMode);

        Loaded += (_, _) =>
        {
            TxtName.SelectAll();
            TxtName.Focus();
        };
    }

    private void TxtName_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Return) TryAccept();
    }

    private void Ok_Click(object sender, RoutedEventArgs e) => TryAccept();

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void TryAccept()
    {
        var name  = TxtName.Text.Trim();
        var error = _validate(name);
        if (error is not null)
        {
            TxtError.Text       = error;
            TxtError.Visibility = Visibility.Visible;
            return;
        }

        EnteredName  = name;
        DialogResult = true;
    }
}
