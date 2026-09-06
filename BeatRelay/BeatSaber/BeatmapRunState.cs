using System.Collections.Generic;

namespace BeatRelay.BeatSaber;

public sealed class BeatmapRunState
{
    public int CurrentScore { get; set; }

    public int CurrentModifiedScore { get; set; }

    public double Accuracy { get; set; }

    public int Combo { get; set; }

    public int MissCount { get; set; }

    public int BadCutCount { get; set; }

    public int ScoredNotes { get; set; }

    public int? KnownMaxModifiedScore { get; set; }

    public double SongProgressRatio { get; set; }

    public bool IsPaused { get; set; }

    public bool IsReplayMode { get; set; }

    public bool HasRecentNotes { get; set; }

    public double SecondsSinceLastNote { get; set; }

    public double SongTimeSeconds { get; set; }

    public bool IsInPlannedBreak { get; set; }

    public bool IsAfterLastNote { get; set; }

    public double BreakWindowStartTimeSeconds { get; set; }

    public double BreakWindowEndTimeSeconds { get; set; }

    public bool HasUpcomingScorableNote { get; set; }

    public double SecondsUntilNextScorableNote { get; set; } = double.PositiveInfinity;

    public bool IsRuntimeBreakExpansionActive { get; set; }

    public bool IsRuntimeEndExpansionActive { get; set; }

    public bool HasReliableRuntimeBreakDetector { get; set; }

    public bool IsFailedWithNoFail { get; set; }

    public IReadOnlyList<string> ActiveModifiers { get; set; } = new List<string>();
}
