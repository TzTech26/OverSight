namespace Oversight.Models;

/// <summary>
/// Controls which display(s) the fullscreen magnifier operates on.
/// </summary>
public enum MonitorMode
{
    /// <summary>Magnify whichever monitor the cursor is currently on; follows the cursor between monitors.</summary>
    ActiveMonitor,

    /// <summary>Treat all monitors as one virtual desktop; the magnified view can pan across the seam.</summary>
    AllMonitors,

    /// <summary>Only ever magnify the primary monitor.</summary>
    PrimaryOnly
}
