namespace Oversight.Models;

/// <summary>
/// Selects which Windows magnification mechanism drives the zoom. The two engines have a
/// hard tradeoff that cannot be reconciled in the OS APIs:
/// </summary>
public enum MagnifierEngine
{
    /// <summary>
    /// Full-screen Magnification transform. The OS scales the desktop with true sub-pixel
    /// precision, so the zoom-in / zoom-out ramps animate smoothly — but it magnifies the
    /// whole desktop (all monitors) and cannot be confined to one, so <see cref="MonitorMode"/>
    /// does not apply.
    /// </summary>
    Fullscreen,

    /// <summary>
    /// Per-monitor windowed magnifier. Confines the zoom to the monitor chosen by
    /// <see cref="MonitorMode"/>, but only accepts whole-pixel capture rectangles, so the
    /// zoom animation judders while it ramps (rock-steady once settled).
    /// </summary>
    Windowed
}
