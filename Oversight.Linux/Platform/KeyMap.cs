using Avalonia.Input;
using Oversight.Models;

namespace Oversight.Linux.Platform;

/// <summary>
/// Windows virtual keys (what settings.json stores) to X11 keysyms and back from Avalonia's keys, plus the MOD_* to X11
/// modifier mapping. Keys without an entry can't be bound on Linux.
/// </summary>
public static class KeyMap
{
    private static readonly Dictionary<uint, nint> Keysyms = Build();

    private static Dictionary<uint, nint> Build()
    {
        var map = new Dictionary<uint, nint>
        {
            [0x08] = 0xFF08, [0x09] = 0xFF09, [0x0C] = 0xFF0B, [0x0D] = 0xFF0D, [0x10] = 0xFFE1, [0x11] = 0xFFE3, [0x12] = 0xFFE9,
            [0x13] = 0xFF13, [0x14] = 0xFFE5, [0x1B] = 0xFF1B, [0x20] = 0x20, [0x21] = 0xFF55, [0x22] = 0xFF56, [0x23] = 0xFF57,
            [0x24] = 0xFF50, [0x25] = 0xFF51, [0x26] = 0xFF52, [0x27] = 0xFF53, [0x28] = 0xFF54, [0x29] = 0xFF60, [0x2A] = 0xFF61,
            [0x2B] = 0xFF62, [0x2C] = 0xFF61, [0x2D] = 0xFF63, [0x2E] = 0xFFFF, [0x2F] = 0xFF6A, [0x5B] = 0xFFEB, [0x5C] = 0xFFEC,
            [0x5D] = 0xFF67, [0x6A] = 0xFFAA, [0x6B] = 0xFFAB, [0x6C] = 0xFFAC, [0x6D] = 0xFFAD, [0x6E] = 0xFFAE, [0x6F] = 0xFFAF,
            [0x90] = 0xFF7F, [0x91] = 0xFF14, [0xA0] = 0xFFE1, [0xA1] = 0xFFE2, [0xA2] = 0xFFE3, [0xA3] = 0xFFE4, [0xA4] = 0xFFE9,
            [0xA5] = 0xFFEA, [0xBA] = 0x3B, [0xBB] = 0x3D, [0xBC] = 0x2C, [0xBD] = 0x2D, [0xBE] = 0x2E, [0xBF] = 0x2F, [0xC0] = 0x60,
            [0xDB] = 0x5B, [0xDC] = 0x5C, [0xDD] = 0x5D, [0xDE] = 0x27, [0xE2] = 0x3C,
            [0xAD] = 0x1008FF12, [0xAE] = 0x1008FF11, [0xAF] = 0x1008FF13, [0xB0] = 0x1008FF17, [0xB1] = 0x1008FF16, [0xB2] = 0x1008FF15, [0xB3] = 0x1008FF14,
        };
        for (uint i = 0; i < 26; i++) map[0x41 + i] = (nint)(0x61 + i);    // A-Z → a-z
        for (uint i = 0; i < 10; i++) map[0x30 + i] = (nint)(0x30 + i);    // 0-9
        for (uint i = 0; i < 10; i++) map[0x60 + i] = (nint)(0xFFB0 + i);  // numpad 0-9
        for (uint i = 0; i < 24; i++) map[0x70 + i] = (nint)(0xFFBE + i);  // F1-F24
        return map;
    }

    /// <summary>The X keycode for a virtual key under the current layout, or 0.</summary>
    public static int Keycode(nint display, uint vk) =>
        display != 0 && Keysyms.TryGetValue(vk, out nint sym) ? X11.XKeysymToKeycode(display, sym) : 0;

    /// <summary>Windows MOD_* flags to X11 modifier masks (Win = Super = Mod4).</summary>
    public static uint Modifiers(uint mods) =>
        ((mods & VirtualKeys.MOD_SHIFT) != 0 ? X11.ShiftMask : 0) | ((mods & VirtualKeys.MOD_CONTROL) != 0 ? X11.ControlMask : 0) |
        ((mods & VirtualKeys.MOD_ALT) != 0 ? X11.Mod1Mask : 0) | ((mods & VirtualKeys.MOD_WIN) != 0 ? X11.Mod4Mask : 0);

    /// <summary>X mouse button for a mouse virtual key (Mouse 4/5 are buttons 8/9), or 0.</summary>
    public static int Button(uint vk) => vk switch
    {
        VirtualKeys.VK_LBUTTON => 1, VirtualKeys.VK_MBUTTON => 2, VirtualKeys.VK_RBUTTON => 3,
        VirtualKeys.VK_XBUTTON1 => 8, VirtualKeys.VK_XBUTTON2 => 9, _ => 0,
    };

    /// <summary>Whether a key or mouse button is physically down (for hold modes). Generic Shift/Ctrl/Alt match either side.</summary>
    public static bool IsDown(uint vk)
    {
        nint d = X11.Display;
        if (Button(vk) is int b and > 0) return X11.ButtonDown(d, b);
        return vk switch
        {
            0x10 => IsDown(0xA0) || IsDown(0xA1),
            0x11 => IsDown(0xA2) || IsDown(0xA3),
            0x12 => IsDown(0xA4) || IsDown(0xA5),
            _ => X11.KeycodeDown(d, Keycode(d, vk)),
        };
    }

    /// <summary>Ctrl, Alt, Shift or Super held (the start/stop combo still down).</summary>
    public static bool ModifiersHeld() => IsDown(0x10) || IsDown(0x11) || IsDown(0x12) || IsDown(0x5B) || IsDown(0x5C);

    /// <summary>The virtual key for a key pressed in an Avalonia window, or 0 when it has none.</summary>
    public static uint FromAvalonia(Key key) => key switch
    {
        >= Key.A and <= Key.Z => 0x41 + (uint)(key - Key.A),
        >= Key.D0 and <= Key.D9 => 0x30 + (uint)(key - Key.D0),
        >= Key.NumPad0 and <= Key.NumPad9 => 0x60 + (uint)(key - Key.NumPad0),
        >= Key.F1 and <= Key.F24 => 0x70 + (uint)(key - Key.F1),
        Key.Back => 0x08, Key.Tab => 0x09, Key.Clear => 0x0C, Key.Enter => 0x0D, Key.Pause => 0x13, Key.CapsLock => 0x14,
        Key.Escape => 0x1B, Key.Space => 0x20, Key.PageUp => 0x21, Key.PageDown => 0x22, Key.End => 0x23, Key.Home => 0x24,
        Key.Left => 0x25, Key.Up => 0x26, Key.Right => 0x27, Key.Down => 0x28, Key.Select => 0x29, Key.Print => 0x2A,
        Key.Execute => 0x2B, Key.PrintScreen => 0x2C, Key.Insert => 0x2D, Key.Delete => 0x2E, Key.Help => 0x2F,
        Key.LWin => 0x5B, Key.RWin => 0x5C, Key.Apps => 0x5D, Key.Multiply => 0x6A, Key.Add => 0x6B, Key.Separator => 0x6C,
        Key.Subtract => 0x6D, Key.Decimal => 0x6E, Key.Divide => 0x6F, Key.NumLock => 0x90, Key.Scroll => 0x91,
        Key.OemSemicolon => 0xBA, Key.OemPlus => 0xBB, Key.OemComma => 0xBC, Key.OemMinus => 0xBD, Key.OemPeriod => 0xBE,
        Key.OemQuestion => 0xBF, Key.OemTilde => 0xC0, Key.OemOpenBrackets => 0xDB, Key.OemPipe => 0xDC,
        Key.OemCloseBrackets => 0xDD, Key.OemQuotes => 0xDE, Key.OemBackslash => 0xE2,
        Key.VolumeMute => 0xAD, Key.VolumeDown => 0xAE, Key.VolumeUp => 0xAF, Key.MediaNextTrack => 0xB0,
        Key.MediaPreviousTrack => 0xB1, Key.MediaStop => 0xB2, Key.MediaPlayPause => 0xB3,
        _ => 0,
    };
}
