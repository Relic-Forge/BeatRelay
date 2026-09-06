using System;

namespace BeatRelay.Ranking;

public sealed class RankSmoother
{
    private int? displayedRank;
    private int? pendingEarlyRank;
    private int stableMovementCycles;

    public SmoothedRank Update(int rawRank, double songProgressRatio)
    {
        if (rawRank <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(rawRank), "Rank must be positive.");
        }

        if (!displayedRank.HasValue)
        {
            displayedRank = rawRank;
            pendingEarlyRank = null;
            stableMovementCycles = 0;
            return new SmoothedRank(rawRank, 0, false);
        }

        var clampedProgress = songProgressRatio < 0 ? 0 : (songProgressRatio > 1 ? 1 : songProgressRatio);
        var previousRank = displayedRank.Value;
        if (clampedProgress >= 0.15)
        {
            displayedRank = rawRank;
            pendingEarlyRank = null;
            stableMovementCycles = 0;
            var movement = previousRank - rawRank;
            return new SmoothedRank(rawRank, movement, movement != 0);
        }

        if (rawRank == previousRank)
        {
            pendingEarlyRank = null;
            stableMovementCycles = 0;
            return new SmoothedRank(previousRank, 0, false);
        }

        if (pendingEarlyRank == rawRank)
        {
            stableMovementCycles++;
        }
        else
        {
            pendingEarlyRank = rawRank;
            stableMovementCycles = 1;
        }

        if (stableMovementCycles < 2)
        {
            return new SmoothedRank(previousRank, 0, false);
        }

        displayedRank = rawRank;
        pendingEarlyRank = null;
        stableMovementCycles = 0;
        var acceptedMovement = previousRank - rawRank;
        return new SmoothedRank(rawRank, acceptedMovement, acceptedMovement != 0);
    }

    public void Reset()
    {
        displayedRank = null;
        pendingEarlyRank = null;
        stableMovementCycles = 0;
    }
}

public sealed class SmoothedRank
{
    public SmoothedRank(int rank, int movement, bool showMovement)
    {
        Rank = rank;
        Movement = movement;
        ShowMovement = showMovement;
    }

    public int Rank { get; }

    public int Movement { get; }

    public bool ShowMovement { get; }
}
