using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Oversight.Models;
using Oversight.Native;
using TextBox = System.Windows.Controls.TextBox;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Cursors = System.Windows.Input.Cursors;

namespace Oversight.UI;

/// <summary>
/// Read-only TextBox that enters a "capture" mode when clicked.
/// While capturing, the next non-modifier key press (+ current modifiers) is
/// recorded as a HotkeyBinding.  Escape clears the binding.
/// </summary>
public sealed class HotkeyCaptureBox : TextBox
{
    public static readonly DependencyProperty BindingValueProperty =
        DependencyProperty.Register(
            nameof(BindingValue), typeof(HotkeyBinding), typeof(HotkeyCaptureBox),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
                OnBindingValueChanged));

    public HotkeyBinding? BindingValue
    {
        get => (HotkeyBinding?)GetValue(BindingValueProperty);
        set => SetValue(BindingValueProperty, value);
    }

    private static void OnBindingValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var box = (HotkeyCaptureBox)d;
        if (!box._capturing)
            box.Text = (e.NewValue as HotkeyBinding)?.DisplayName ?? "(none)";
    }

    private bool _capturing;

    /// <summary>
    /// When true, the box also records mouse buttons (right / middle / Mouse 4 / Mouse 5 —
    /// never left, which is needed to operate the UI). Used by binds that are watched via
    /// polling rather than RegisterHotKey, e.g. the crosshair "hide while held" key.
    /// </summary>
    public bool AllowMouseButtons { get; set; }

    /// <summary>Raised when the box starts listening for a key (focus enters capture mode).</summary>
    public event Action? CaptureStarted;

    /// <summary>Raised when capture mode ends (key recorded, cleared, or focus lost).</summary>
    public event Action? CaptureStopped;

    public HotkeyCaptureBox()
    {
        IsReadOnly    = true;
        IsReadOnlyCaretVisible = false;
        Cursor        = Cursors.Arrow;
        Text          = "(none)";
        TextAlignment = TextAlignment.Center;

        LostFocus           += (_, _) => CancelCapture();
    }

    // A WPF TextBox swallows the bubbling MouseLeftButtonDown event, so we must
    // start capture from the tunneling preview event instead.
    protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        StartCapture();
        e.Handled = true;
        base.OnPreviewMouseLeftButtonDown(e);
    }

    // Records a mouse button while capturing (only when opted in via AllowMouseButtons).
    protected override void OnPreviewMouseDown(MouseButtonEventArgs e)
    {
        if (_capturing && AllowMouseButtons && e.ChangedButton != MouseButton.Left)
        {
            uint vk = e.ChangedButton switch
            {
                MouseButton.Right    => 0x02,
                MouseButton.Middle   => 0x04,
                MouseButton.XButton1 => 0x05,
                MouseButton.XButton2 => 0x06,
                _                    => 0,
            };
            if (vk != 0)
            {
                StopCapture();
                BindingValue = new HotkeyBinding { Modifiers = 0, VirtualKey = vk };
                Text = BindingValue.DisplayName;
                e.Handled = true;
                return;
            }
        }
        base.OnPreviewMouseDown(e);
    }

    // A right-click that just recorded a binding must not also pop the TextBox context menu.
    protected override void OnContextMenuOpening(ContextMenuEventArgs e)
    {
        if (AllowMouseButtons)
        {
            e.Handled = true;
            return;
        }
        base.OnContextMenuOpening(e);
    }

    private void StartCapture()
    {
        if (_capturing) return;
        _capturing = true;
        Text       = AllowMouseButtons ? "Press key / mouse button..." : "Press a key...";
        Focus();
        CaptureStarted?.Invoke();
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
        Text = BindingValue?.DisplayName ?? "(none)";
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (!_capturing) { base.OnPreviewKeyDown(e); return; }
        e.Handled = true;

        // Ignore bare modifier key presses — wait for a real key
        var k = e.Key == Key.System ? e.SystemKey : e.Key;
        if (k is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
                or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
            return;

        StopCapture();

        if (k == Key.Escape)
        {
            BindingValue = new HotkeyBinding();
            Text = "(none)";
            return;
        }

        var mods = Keyboard.Modifiers;
        uint modFlags = 0;
        if ((mods & ModifierKeys.Control) != 0) modFlags |= NativeMethods.MOD_CONTROL;
        if ((mods & ModifierKeys.Alt)     != 0) modFlags |= NativeMethods.MOD_ALT;
        if ((mods & ModifierKeys.Shift)   != 0) modFlags |= NativeMethods.MOD_SHIFT;
        if ((mods & ModifierKeys.Windows) != 0) modFlags |= NativeMethods.MOD_WIN;

        uint vk = (uint)KeyInterop.VirtualKeyFromKey(k);
        BindingValue = new HotkeyBinding { Modifiers = modFlags, VirtualKey = vk };
        Text = BindingValue.DisplayName;
    }
}
