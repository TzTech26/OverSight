namespace Oversight.Models;

/// <summary>
/// When the auto-clicker stops on its own after it has been started.
/// </summary>
public enum RepeatMode
{
    /// <summary>Keep clicking until the user stops it (or the app loses focus).</summary>
    UntilStopped,

    /// <summary>Stop automatically after a fixed number of seconds.</summary>
    ForDuration,

    /// <summary>Stop automatically after a fixed number of clicks.</summary>
    ForCount
}
