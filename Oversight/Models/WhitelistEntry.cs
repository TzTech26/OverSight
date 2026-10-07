namespace Oversight.Models;

public class WhitelistEntry
{
    /// <summary>Process name without .exe extension, case-insensitive match.</summary>
    public string ProcessName { get; set; } = string.Empty;

    /// <summary>Display label (defaults to ProcessName if empty).</summary>
    public string Label { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;

    public string DisplayLabel => string.IsNullOrWhiteSpace(Label) ? ProcessName : Label;
}
