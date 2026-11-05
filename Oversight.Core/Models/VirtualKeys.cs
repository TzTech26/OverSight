namespace Oversight.Models;

/// <summary>
/// Windows virtual-key codes and MOD_* flags. settings.json stores keys in this form on every platform; the Linux app
/// maps them to X11 keysyms.
/// </summary>
public static class VirtualKeys
{
    public const uint MOD_ALT      = 0x0001;
    public const uint MOD_CONTROL  = 0x0002;
    public const uint MOD_SHIFT    = 0x0004;
    public const uint MOD_WIN      = 0x0008;
    public const uint MOD_NOREPEAT = 0x4000;

    public const uint VK_LBUTTON = 0x01, VK_RBUTTON = 0x02, VK_MBUTTON = 0x04, VK_XBUTTON1 = 0x05, VK_XBUTTON2 = 0x06;
    public const uint VK_C = 0x43, VK_X = 0x58, VK_Z = 0x5A, VK_F8 = 0x77, VK_F12 = 0x7B;
    public const uint VK_OEM_PLUS = 0xBB, VK_OEM_MINUS = 0xBD;

    /// <summary>Key names as the Windows app has always shown them (WPF Key names plus a few friendlier ones).</summary>
    public static string Name(uint vk) => Names.TryGetValue(vk, out var name) ? name : $"VK_0x{vk:X2}";

    // Generated from WPF's KeyInterop.KeyFromVirtualKey + Key.ToString(), with the old friendly overrides applied.
    private static readonly Dictionary<uint, string> Names = new()
    {
        [0x03] = "Cancel", [0x08] = "Back", [0x09] = "Tab", [0x0C] = "Clear", [0x0D] = "Enter", [0x10] = "LeftShift",
        [0x11] = "LeftCtrl", [0x12] = "LeftAlt", [0x13] = "Pause", [0x14] = "CapsLock", [0x15] = "KanaMode", [0x17] = "JunjaMode",
        [0x18] = "FinalMode", [0x19] = "HanjaMode", [0x1B] = "Esc", [0x1C] = "ImeConvert", [0x1D] = "ImeNonConvert", [0x1E] = "ImeAccept",
        [0x1F] = "ImeModeChange", [0x20] = "Space", [0x21] = "PageUp", [0x22] = "PageDown", [0x23] = "End", [0x24] = "Home",
        [0x25] = "Left", [0x26] = "Up", [0x27] = "Right", [0x28] = "Down", [0x29] = "Select", [0x2A] = "Print",
        [0x2B] = "Execute", [0x2C] = "Snapshot", [0x2D] = "Insert", [0x2E] = "Delete", [0x2F] = "Help", [0x30] = "0",
        [0x31] = "1", [0x32] = "2", [0x33] = "3", [0x34] = "4", [0x35] = "5", [0x36] = "6",
        [0x37] = "7", [0x38] = "8", [0x39] = "9", [0x41] = "A", [0x42] = "B", [0x43] = "C",
        [0x44] = "D", [0x45] = "E", [0x46] = "F", [0x47] = "G", [0x48] = "H", [0x49] = "I",
        [0x4A] = "J", [0x4B] = "K", [0x4C] = "L", [0x4D] = "M", [0x4E] = "N", [0x4F] = "O",
        [0x50] = "P", [0x51] = "Q", [0x52] = "R", [0x53] = "S", [0x54] = "T", [0x55] = "U",
        [0x56] = "V", [0x57] = "W", [0x58] = "X", [0x59] = "Y", [0x5A] = "Z", [0x5B] = "LWin",
        [0x5C] = "RWin", [0x5D] = "Apps", [0x5F] = "Sleep", [0x60] = "Num 0", [0x61] = "Num 1", [0x62] = "Num 2",
        [0x63] = "Num 3", [0x64] = "Num 4", [0x65] = "Num 5", [0x66] = "Num 6", [0x67] = "Num 7", [0x68] = "Num 8",
        [0x69] = "Num 9", [0x6A] = "Multiply", [0x6B] = "Add", [0x6C] = "Separator", [0x6D] = "Subtract", [0x6E] = "Decimal",
        [0x6F] = "Divide", [0x70] = "F1", [0x71] = "F2", [0x72] = "F3", [0x73] = "F4", [0x74] = "F5",
        [0x75] = "F6", [0x76] = "F7", [0x77] = "F8", [0x78] = "F9", [0x79] = "F10", [0x7A] = "F11",
        [0x7B] = "F12", [0x7C] = "F13", [0x7D] = "F14", [0x7E] = "F15", [0x7F] = "F16", [0x80] = "F17",
        [0x81] = "F18", [0x82] = "F19", [0x83] = "F20", [0x84] = "F21", [0x85] = "F22", [0x86] = "F23",
        [0x87] = "F24", [0x90] = "NumLock", [0x91] = "Scroll", [0xA0] = "LeftShift", [0xA1] = "RightShift", [0xA2] = "LeftCtrl",
        [0xA3] = "RightCtrl", [0xA4] = "LeftAlt", [0xA5] = "RightAlt", [0xA6] = "BrowserBack", [0xA7] = "BrowserForward", [0xA8] = "BrowserRefresh",
        [0xA9] = "BrowserStop", [0xAA] = "BrowserSearch", [0xAB] = "BrowserFavorites", [0xAC] = "BrowserHome", [0xAD] = "VolumeMute", [0xAE] = "VolumeDown",
        [0xAF] = "VolumeUp", [0xB0] = "MediaNextTrack", [0xB1] = "MediaPreviousTrack", [0xB2] = "MediaStop", [0xB3] = "MediaPlayPause", [0xB4] = "LaunchMail",
        [0xB5] = "SelectMedia", [0xB6] = "LaunchApplication1", [0xB7] = "LaunchApplication2", [0xBA] = "Oem1", [0xBB] = "=", [0xBC] = "OemComma",
        [0xBD] = "-", [0xBE] = "OemPeriod", [0xBF] = "OemQuestion", [0xC0] = "`", [0xC1] = "AbntC1", [0xC2] = "AbntC2",
        [0xDB] = "OemOpenBrackets", [0xDC] = "Oem5", [0xDD] = "Oem6", [0xDE] = "OemQuotes", [0xDF] = "Oem8", [0xE2] = "OemBackslash",
        [0xE5] = "ImeProcessed", [0xF0] = "OemAttn", [0xF1] = "OemFinish", [0xF2] = "OemCopy", [0xF3] = "DbeSbcsChar", [0xF4] = "OemEnlw",
        [0xF5] = "OemBackTab", [0xF6] = "DbeNoRoman", [0xF7] = "DbeEnterWordRegisterMode", [0xF8] = "DbeEnterImeConfigureMode", [0xF9] = "EraseEof", [0xFA] = "Play",
        [0xFB] = "DbeNoCodeInput", [0xFC] = "NoName", [0xFD] = "Pa1", [0xFE] = "OemClear",
    };
}
