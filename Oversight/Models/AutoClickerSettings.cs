namespace Oversight.Models;

/// <summary>
/// One auto-clicker save slot: the key or mouse button to repeat, the timing, and the
/// stop-condition. Like <see cref="ZoomSettings"/> / <see cref="CrosshairSettings"/> this is
/// the per-slot document; the start/stop keybind lives on <see cref="AutoClickerProfile"/>
/// (so it travels with the slot, and each game's slot can use a different key).
///
/// <para><b>Safety.</b> Nothing here makes the clicker run — it only clicks after the user
/// assigns a slot to an app in Game Profiles and presses the start/stop bind. See
/// <see cref="Services.AutoClickerService"/>.</para>
/// </summary>
public class AutoClickerSettings
{
    /// <summary>
    /// The key or mouse button pressed on every tick. Only <see cref="HotkeyBinding.VirtualKey"/>
    /// is used (modifiers are ignored). Mouse buttons are the VKs recognised by
    /// <see cref="HotkeyBinding.IsMouseButton"/>. Defaults to the left mouse button.
    /// </summary>
    public HotkeyBinding ActionKey { get; set; } = new() { VirtualKey = 0x01 };

    /// <summary>How the start/stop bind behaves: press-to-toggle, or click only while held.</summary>
    public ActivationMode Activation { get; set; } = ActivationMode.Toggle;

    /// <summary>Base delay between clicks, in milliseconds.</summary>
    public int IntervalMs { get; set; } = 100;

    /// <summary>When true, each interval is randomised by ± <see cref="JitterMs"/> so the timing isn't perfectly robotic.</summary>
    public bool JitterEnabled { get; set; } = false;

    /// <summary>Maximum random offset added to / subtracted from each interval, in milliseconds.</summary>
    public int JitterMs { get; set; } = 0;

    /// <summary>
    /// When true (the default), the click loop watches whether the target window is keeping up
    /// and automatically backs off when it stops responding — so very fast rates can't flood and
    /// freeze the app. Turn off only for raw, uncapped speed (e.g. benchmarking / promo). See
    /// <see cref="Services.AutoClickerService"/>.
    /// </summary>
    public bool AdaptiveLimit { get; set; } = true;

    /// <summary>Single vs double click per tick — only meaningful when <see cref="ActionKey"/> is a mouse button.</summary>
    public MouseClickType ClickType { get; set; } = MouseClickType.Single;

    /// <summary>When the clicker stops on its own: never, after a duration, or after a click count.</summary>
    public RepeatMode RepeatMode { get; set; } = RepeatMode.UntilStopped;

    /// <summary>Run length for <see cref="RepeatMode.ForDuration"/>, in seconds.</summary>
    public int RepeatSeconds { get; set; } = 30;

    /// <summary>Click count for <see cref="RepeatMode.ForCount"/>.</summary>
    public int RepeatCount { get; set; } = 100;

    /// <summary>When true, clicks land at <see cref="FixedX"/>,<see cref="FixedY"/> instead of the live cursor position.</summary>
    public bool UseFixedPosition { get; set; } = false;

    /// <summary>Fixed click point X (physical pixels). Only used when <see cref="UseFixedPosition"/>.</summary>
    public int FixedX { get; set; } = 0;

    /// <summary>Fixed click point Y (physical pixels). Only used when <see cref="UseFixedPosition"/>.</summary>
    public int FixedY { get; set; } = 0;

    /// <summary>When true, starting the clicker waits <see cref="SafetyCountdownSeconds"/> (with a tray notice) before the first click.</summary>
    public bool SafetyCountdownEnabled { get; set; } = false;

    /// <summary>Delay before the first click when <see cref="SafetyCountdownEnabled"/>, in seconds.</summary>
    public int SafetyCountdownSeconds { get; set; } = 3;

    // ── Copy helpers ──────────────────────────────────────────────────────────

    public AutoClickerSettings Clone()
    {
        var dst = new AutoClickerSettings();
        dst.CopyFrom(this);
        return dst;
    }

    public void CopyFrom(AutoClickerSettings src)
    {
        ActionKey              = new HotkeyBinding { Modifiers = src.ActionKey.Modifiers, VirtualKey = src.ActionKey.VirtualKey };
        Activation             = src.Activation;
        IntervalMs             = src.IntervalMs;
        JitterEnabled          = src.JitterEnabled;
        JitterMs               = src.JitterMs;
        AdaptiveLimit          = src.AdaptiveLimit;
        ClickType              = src.ClickType;
        RepeatMode             = src.RepeatMode;
        RepeatSeconds          = src.RepeatSeconds;
        RepeatCount            = src.RepeatCount;
        UseFixedPosition       = src.UseFixedPosition;
        FixedX                 = src.FixedX;
        FixedY                 = src.FixedY;
        SafetyCountdownEnabled = src.SafetyCountdownEnabled;
        SafetyCountdownSeconds = src.SafetyCountdownSeconds;
    }
}
