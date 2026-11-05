namespace Oversight.Models;

/// <summary>
/// How each auto-clicker tick clicks, when the action key is a mouse button. Ignored for
/// keyboard action keys (a key is always a single down+up press).
/// </summary>
public enum MouseClickType
{
    /// <summary>One press per tick.</summary>
    Single,

    /// <summary>Two quick presses per tick (a double-click).</summary>
    Double
}
