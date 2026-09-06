using System.Collections.Generic;
using BeatRelay.BeatLeader;

namespace BeatRelay.Ranking;

public sealed class ProjectionResult
{
    public bool HasProjection { get; set; }

    public int? ProjectedRank { get; set; }

    public int? ProjectedFinalScore { get; set; }

    public double? ProjectedPp { get; set; }

    public double? RankingPp { get; set; }

    public double? LiveAccuracy { get; set; }

    public bool ExactRankCovered { get; set; }

    public int? SuggestedPageToFetch { get; set; }

    public string Label { get; set; } = string.Empty;

    public IReadOnlyList<BeatLeaderScoreRow> NearbyRows { get; set; } = new List<BeatLeaderScoreRow>();
}
