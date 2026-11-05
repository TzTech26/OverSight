namespace Oversight.Models;

/// <summary>
/// How the "Toggle zoom" hotkey behaves.
/// </summary>
public enum ActivationMode
{
    /// <summary>Press once to turn zoom on, press again to turn it off (the classic behaviour).</summary>
    Toggle,

    /// <summary>Zoom stays on only while the key is held down; releasing it turns zoom off.</summary>
    Hold
}
