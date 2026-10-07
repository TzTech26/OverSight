using System.Text;
using System.Windows.Input;
using Oversight.Native;

namespace Oversight.Models;

public class HotkeyBinding
{
    /// <summary>Win32 MOD_* flags (MOD_CONTROL, MOD_ALT, MOD_SHIFT, MOD_WIN).</summary>
    public uint Modifiers { get; set; } = 0;

    /// <summary>Win32 virtual-key code. 0 means unbound.</summary>
    public uint VirtualKey { get; set; } = 0;

    public bool IsEmpty => VirtualKey == 0;

    public string DisplayName => FormatHotkey(Modifiers, VirtualKey);

    /// <summary>
    /// True when <paramref name="vk"/> is a mouse-button virtual key. Mouse buttons cannot go
    /// through RegisterHotKey — bindings holding one are watched by polling the button state
    /// (see CrosshairService) and skipped by the hotkey manager.
    /// </summary>
    public static bool IsMouseButton(uint vk) =>
        vk is 0x01 or 0x02 or 0x04 or 0x05 or 0x06;   // L, R, Middle, X1, X2 (0x03 is VK_CANCEL)

    public static string FormatHotkey(uint modifiers, uint vk)
    {
        if (vk == 0) return "(none)";
        var sb = new StringBuilder();
        if ((modifiers & NativeMethods.MOD_CONTROL) != 0) sb.Append("Ctrl+");
        if ((modifiers & NativeMethods.MOD_ALT)     != 0) sb.Append("Alt+");
        if ((modifiers & NativeMethods.MOD_SHIFT)   != 0) sb.Append("Shift+");
        if ((modifiers & NativeMethods.MOD_WIN)     != 0) sb.Append("Win+");

        string? mouse = vk switch
        {
            0x01 => "Left Mouse",
            0x02 => "Right Mouse",
            0x04 => "Middle Mouse",
            0x05 => "Mouse 4",
            0x06 => "Mouse 5",
            _    => null,
        };
        if (mouse is not null)
        {
            sb.Append(mouse);
            return sb.ToString();
        }

        var key = KeyInterop.KeyFromVirtualKey((int)vk);
        sb.Append(key == Key.None ? $"VK_0x{vk:X2}" : key.ToString());
        return sb.ToString();
    }
}
