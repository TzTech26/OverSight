namespace Oversight.Models;

/// <summary>A named zoom save slot (zoom behaviour + zoom keybinds).</summary>
public class ZoomProfile
{
    public string Name { get; set; } = "Default";

    public ZoomSettings Settings { get; set; } = new();
}
