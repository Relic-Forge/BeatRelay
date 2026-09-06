namespace BeatRelay.Sessions;

public sealed class BeatmapSessionInfo
{
    public string Hash { get; set; } = string.Empty;

    public string Difficulty { get; set; } = string.Empty;

    public string Mode { get; set; } = "Standard";

    public string? SongName { get; set; }

    public string? MapperName { get; set; }

    public IReadOnlyList<string> ActiveModifiers { get; set; } = new List<string>();

    public IReadOnlyList<double> NoteTimes { get; set; } = new List<double>();

    public object? RuntimeBeatmapData { get; set; }

    public double? SongLengthSeconds { get; set; }

    public double? BeatsPerMinute { get; set; }

    public bool IsReplayMode { get; set; }

    public bool IsPracticeMode { get; set; }

    public bool HudSuppressedByGame { get; set; }

    public long? ReplayScoreId { get; set; }
}
