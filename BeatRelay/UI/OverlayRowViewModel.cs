namespace BeatRelay.UI;

public sealed class OverlayRowViewModel
{
    public int Rank { get; set; }

    public string PlayerId { get; set; } = string.Empty;

    public string PlayerName { get; set; } = string.Empty;

    public string ValueText { get; set; } = string.Empty;

    public string Modifiers { get; set; } = string.Empty;

    public double? Accuracy { get; set; }

    public int? Score { get; set; }

    public bool IsLocalPlayer { get; set; }

    public bool IsProjected { get; set; }
}
