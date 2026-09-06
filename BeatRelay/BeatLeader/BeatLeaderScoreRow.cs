namespace BeatRelay.BeatLeader;

public sealed class BeatLeaderScoreRow
{
    public long ScoreId { get; set; }

    public int Rank { get; set; }

    public int? BaseScore { get; set; }

    public int ModifiedScore { get; set; }

    public double? Accuracy { get; set; }

    public double? Pp { get; set; }

    public string Modifiers { get; set; } = string.Empty;

    public string PlayerId { get; set; } = string.Empty;

    public string PlayerName { get; set; } = "Unknown Player";

    public string AvatarUrl { get; set; } = string.Empty;
}
