using System;
using BeatRelay.BeatSaber;

namespace BeatRelay.Ranking;

public sealed class ConfidenceEngine
{
    private int? previousRank;
    private int stableCycles;

    public ConfidenceDecision Evaluate(BeatmapRunState runState, ProjectionResult projection)
    {
        if (runState == null)
        {
            throw new ArgumentNullException(nameof(runState));
        }

        if (projection == null)
        {
            throw new ArgumentNullException(nameof(projection));
        }

        if (!projection.HasProjection || !projection.ProjectedRank.HasValue)
        {
            return ConfidenceDecision.Hidden("--");
        }

        var progressGatePassed = runState.SongProgressRatio >= 0.04 || runState.ScoredNotes >= 20;
        if (!progressGatePassed)
        {
            TrackStability(projection.ProjectedRank.Value);
            return ConfidenceDecision.Hidden($"#{projection.ProjectedRank.Value}");
        }

        var numericGatePassed = runState.SongProgressRatio >= 0.08 || runState.ScoredNotes >= 45;
        if (!numericGatePassed)
        {
            TrackStability(projection.ProjectedRank.Value);
            return ConfidenceDecision.Range($"#{projection.ProjectedRank.Value}");
        }

        if (projection.ProjectedRank.Value == previousRank)
        {
            stableCycles++;
        }
        else
        {
            previousRank = projection.ProjectedRank.Value;
            stableCycles = 1;
        }

        if (stableCycles < 2)
        {
            return ConfidenceDecision.Hidden($"#{projection.ProjectedRank.Value}");
        }

        return ConfidenceDecision.Numeric($"#{projection.ProjectedRank.Value}");
    }

    public void Reset()
    {
        previousRank = null;
        stableCycles = 0;
    }

    private void TrackStability(int rank)
    {
        if (previousRank == rank)
        {
            stableCycles++;
            return;
        }

        previousRank = rank;
        stableCycles = 1;
    }

}

public sealed class ConfidenceDecision
{
    private ConfidenceDecision(bool canShowNumericRank, bool canShowRange, string displayText)
    {
        CanShowNumericRank = canShowNumericRank;
        CanShowRange = canShowRange;
        DisplayText = displayText;
    }

    public bool CanShowNumericRank { get; }

    public bool CanShowRange { get; }

    public string DisplayText { get; }

    public static ConfidenceDecision Hidden(string displayText) => new(false, false, displayText);

    public static ConfidenceDecision Range(string displayText) => new(false, true, displayText);

    public static ConfidenceDecision Numeric(string displayText) => new(true, true, displayText);
}
