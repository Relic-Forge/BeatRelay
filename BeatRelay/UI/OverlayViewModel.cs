using System.Collections.Generic;

namespace BeatRelay.UI;

public sealed class OverlayViewModel
{
    public OverlayMode Mode { get; set; } = OverlayMode.Hidden;

    public string DisplayName { get; set; } = string.Empty;

    public string RankText { get; set; } = string.Empty;

    public string MovementText { get; set; } = string.Empty;

    public string MessageText { get; set; } = string.Empty;

    public string MapContextText { get; set; } = string.Empty;

    public string ModifiersText { get; set; } = string.Empty;

    public string SourceName { get; set; } = string.Empty;

    public bool IsRanked { get; set; }

    public bool SupportsPp { get; set; }

    public int? ProjectedScore { get; set; }

    public bool AnimateRankChange { get; set; }

    public bool NoFailPenaltyActive { get; set; }

    public IReadOnlyList<OverlayRowViewModel> Rows { get; set; } = new List<OverlayRowViewModel>();

    public static OverlayViewModel Hidden() => new() { Mode = OverlayMode.Hidden };

    public static OverlayViewModel Unavailable(string message) => new()
    {
        Mode = OverlayMode.Unavailable,
        MessageText = message
    };
}
