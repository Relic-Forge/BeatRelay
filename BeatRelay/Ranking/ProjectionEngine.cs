using System;
using System.Linq;
using BeatRelay.BeatLeader;
using BeatRelay.BeatSaber;
using BeatRelay.ScoreSaber;

namespace BeatRelay.Ranking;

public sealed class ProjectionEngine
{
    private const int SparseSegmentProbeMinimumRows = 50;

    private static readonly (double Accuracy, double Value)[] BeatLeaderAccCurve =
    {
        (1.0, 7.424),
        (0.999, 6.241),
        (0.9975, 5.158),
        (0.995, 4.010),
        (0.9925, 3.241),
        (0.99, 2.700),
        (0.9875, 2.303),
        (0.985, 2.007),
        (0.9825, 1.786),
        (0.98, 1.618),
        (0.9775, 1.490),
        (0.975, 1.392),
        (0.9725, 1.315),
        (0.97, 1.256),
        (0.965, 1.167),
        (0.96, 1.094),
        (0.955, 1.039),
        (0.95, 1.000),
        (0.94, 0.931),
        (0.93, 0.867),
        (0.92, 0.813),
        (0.91, 0.768),
        (0.9, 0.729),
        (0.875, 0.650),
        (0.85, 0.581),
        (0.825, 0.522),
        (0.8, 0.473),
        (0.75, 0.404),
        (0.7, 0.345),
        (0.65, 0.296),
        (0.6, 0.256),
        (0.0, 0.0)
    };

    public ProjectionResult Project(BeatmapRunState runState, LeaderboardSessionCache cache, bool useRawPpRank = false)
    {
        if (runState == null)
        {
            throw new ArgumentNullException(nameof(runState));
        }

        if (cache == null)
        {
            throw new ArgumentNullException(nameof(cache));
        }

        if (!cache.HasUsableRows)
        {
            return new ProjectionResult
            {
                Label = string.Empty,
                LiveAccuracy = Clamp01(runState.Accuracy)
            };
        }

        if (ResolveCurrentRankingScore(runState, cache) <= 0)
        {
            return new ProjectionResult
            {
                Label = string.Empty,
                LiveAccuracy = Clamp01(runState.Accuracy)
            };
        }

        var projectedFinalScore = ComputeProjectedScore(runState, cache);
        var knownMaxModifiedScore = ResolveKnownMaxModifiedScore(runState, cache);
        if (knownMaxModifiedScore.HasValue && !IsBeatLeaderUnrankedCache(cache))
        {
            projectedFinalScore = Math.Min(projectedFinalScore, knownMaxModifiedScore.Value);
        }

        var rankingScore = ComputeRankingScore(runState, cache, projectedFinalScore);

        var rows = cache.ScoresByRank.Values
            .OrderBy(row => row.Rank)
            .ToList();
        var projectedPp = cache.SupportsPp
            ? EstimateRankedProjectedPp(runState, cache, rankingScore, projectedFinalScore, rows)
            : null;
        var rankingPp = cache.RankByPp && useRawPpRank
            ? projectedPp
            : null;
        var usePpRanking = cache.RankByPp && useRawPpRank && rankingPp.HasValue;
        var useAccuracyRanking = IsScoreSaberCache(cache);
        var rankingAccuracy = ResolveRankingAccuracy(runState, cache, rankingScore);
        var insertion = ResolveInsertionRank(
            rows,
            rankingScore,
            rankingPp,
            rankingAccuracy,
            usePpRanking,
            useAccuracyRanking,
            cache.TotalScores,
            cache.ItemsPerPage);
        var insertionRank = insertion.Rank;

        var exactCovered = cache.HasCoverageAroundRank(insertionRank, 1);
        return new ProjectionResult
        {
            HasProjection = true,
            ProjectedRank = insertionRank,
            ProjectedFinalScore = rankingScore,
            ProjectedPp = projectedPp,
            RankingPp = rankingPp,
            LiveAccuracy = Clamp01(runState.Accuracy),
            ExactRankCovered = exactCovered && insertion.Exact,
            SuggestedPageToFetch = insertion.SuggestedRankToFetch.HasValue
                ? cache.GetPageForRank(insertion.SuggestedRankToFetch.Value)
                : (exactCovered ? null : cache.GetPageForRank(insertionRank)),
            Label = $"#{insertionRank}",
            NearbyRows = cache.GetNearbyRows(insertionRank, 1)
        };
    }

    private static InsertionRank ResolveInsertionRank(
        System.Collections.Generic.IReadOnlyList<BeatLeader.BeatLeaderScoreRow> rows,
        int projectedFinalScore,
        double? rankingPp,
        double rankingAccuracy,
        bool usePpRanking,
        bool useAccuracyRanking,
        int totalScores,
        int itemsPerPage)
    {
        if (rows.Count == 0)
        {
            return new InsertionRank(1, false, null);
        }

        var segments = BuildKnownRankSegments(rows);
        var maxSmallGap = Math.Max(1, itemsPerPage - 1);
        for (var i = 0; i < segments.Count; i++)
        {
            var segment = segments[i];
            foreach (var row in segment)
            {
                if (IsBetterOrEqual(projectedFinalScore, rankingPp, rankingAccuracy, row, usePpRanking, useAccuracyRanking))
                {
                    return new InsertionRank(row.Rank, true, null);
                }
            }

            var boundaryRank = segment[segment.Count - 1].Rank + 1;
            if (totalScores > 0)
            {
                boundaryRank = Math.Min(boundaryRank, totalScores + 1);
            }

            if (i + 1 < segments.Count)
            {
                var nextSegment = segments[i + 1];
                var nextKnownRank = nextSegment[0].Rank;
                var missingRanksBetweenSegments = Math.Max(0, nextKnownRank - boundaryRank);
                if (missingRanksBetweenSegments <= maxSmallGap)
                {
                    continue;
                }

                var nextKnownRow = nextSegment[0];
                if (nextSegment.Count >= SparseSegmentProbeMinimumRows
                    && !IsBetterOrEqual(projectedFinalScore, rankingPp, rankingAccuracy, nextKnownRow, usePpRanking, useAccuracyRanking))
                {
                    continue;
                }
            }

            return new InsertionRank(boundaryRank, false, boundaryRank);
        }

        var fallbackRank = Math.Max(1, rows[rows.Count - 1].Rank + 1);
        if (totalScores > 0)
        {
            fallbackRank = Math.Min(fallbackRank, totalScores + 1);
        }

        return new InsertionRank(fallbackRank, false, fallbackRank);
    }

    private readonly struct InsertionRank
    {
        public InsertionRank(int rank, bool exact, int? suggestedRankToFetch)
        {
            Rank = rank;
            Exact = exact;
            SuggestedRankToFetch = suggestedRankToFetch;
        }

        public int Rank { get; }

        public bool Exact { get; }

        public int? SuggestedRankToFetch { get; }
    }

    private static System.Collections.Generic.IReadOnlyList<System.Collections.Generic.IReadOnlyList<BeatLeader.BeatLeaderScoreRow>> BuildKnownRankSegments(
        System.Collections.Generic.IReadOnlyList<BeatLeader.BeatLeaderScoreRow> rows)
    {
        var segments = new System.Collections.Generic.List<System.Collections.Generic.IReadOnlyList<BeatLeader.BeatLeaderScoreRow>>();
        var current = new System.Collections.Generic.List<BeatLeader.BeatLeaderScoreRow>();
        var expectedRank = -1;
        foreach (var row in rows)
        {
            if (current.Count == 0 || row.Rank == expectedRank)
            {
                current.Add(row);
                expectedRank = row.Rank + 1;
                continue;
            }

            segments.Add(current);
            current = new System.Collections.Generic.List<BeatLeader.BeatLeaderScoreRow> { row };
            expectedRank = row.Rank + 1;
        }

        if (current.Count > 0)
        {
            segments.Add(current);
        }

        return segments;
    }

    private static int ComputeProjectedScore(BeatmapRunState runState, LeaderboardSessionCache cache)
    {
        var currentScore = ResolveCurrentRankingScore(runState, cache);
        if (currentScore <= 0)
        {
            return 0;
        }

        var liveAccuracy = Clamp01(runState.Accuracy);
        if (liveAccuracy <= 0)
        {
            return currentScore;
        }

        if (IsBeatLeaderUnrankedCache(cache))
        {
            return ComputeBeatLeaderUnrankedClassicScore(runState, cache, liveAccuracy);
        }

        var knownMax = ResolveKnownMaxModifiedScore(runState, cache).GetValueOrDefault();
        if (knownMax <= 0)
        {
            return currentScore;
        }

        var scoreSaberMode = IsScoreSaberCache(cache);
        if (!HasActiveScoreModifiers(runState) && (scoreSaberMode || !cache.Ranked))
        {
            return currentScore;
        }

        var projected = (int)Math.Round(knownMax * liveAccuracy, MidpointRounding.AwayFromZero);
        if (scoreSaberMode)
        {
            projected = ApplyScoreModifierMultiplier(projected, cache);
        }

        return Math.Max(currentScore, projected);
    }

    private static int ComputeRankingScore(BeatmapRunState runState, LeaderboardSessionCache cache, int projectedFinalScore)
    {
        if (IsBeatLeaderUnrankedCache(cache))
        {
            return Math.Max(0, projectedFinalScore);
        }

        return Math.Max(ResolveCurrentRankingScore(runState, cache), projectedFinalScore);
    }

    private static bool IsBeatLeaderUnrankedCache(LeaderboardSessionCache cache)
    {
        return !IsScoreSaberCache(cache)
            && !cache.Ranked;
    }

    private static bool IsScoreSaberCache(LeaderboardSessionCache cache)
    {
        return string.Equals(cache.SourceName, "ScoreSaber", StringComparison.OrdinalIgnoreCase);
    }

    private static int ResolveCurrentRankingScore(BeatmapRunState runState, LeaderboardSessionCache cache)
    {
        if (!IsScoreSaberCache(cache))
        {
            return Math.Max(0, runState.CurrentModifiedScore);
        }

        var baseScore = runState.CurrentScore > 0 ? runState.CurrentScore : runState.CurrentModifiedScore;
        var multiplier = Math.Max(0d, cache.ScoreModifierMultiplier);
        if (Math.Abs(multiplier - 1d) <= 0.0001d)
        {
            return Math.Max(0, baseScore);
        }

        return ApplyScoreModifierMultiplier(baseScore, cache);
    }

    private static int ApplyScoreModifierMultiplier(int score, LeaderboardSessionCache cache)
    {
        var multiplier = Math.Max(0d, cache.ScoreModifierMultiplier);
        if (Math.Abs(multiplier - 1d) <= 0.0001d)
        {
            return Math.Max(0, score);
        }

        return Math.Max(0, (int)Math.Round(score * multiplier, MidpointRounding.AwayFromZero));
    }

    private static double ResolveRankingAccuracy(BeatmapRunState runState, LeaderboardSessionCache cache, int rankingScore)
    {
        var liveAccuracy = Clamp01(runState.Accuracy);
        if (!IsScoreSaberCache(cache) || !HasEffectiveScoreModifier(cache))
        {
            return liveAccuracy;
        }

        var knownMax = ResolveKnownMaxModifiedScore(runState, cache).GetValueOrDefault();
        if (knownMax <= 0 || rankingScore <= 0)
        {
            return liveAccuracy;
        }

        return Clamp01((double)rankingScore / knownMax);
    }

    private static bool HasEffectiveScoreModifier(LeaderboardSessionCache cache)
    {
        return Math.Abs(cache.ScoreModifierMultiplier - 1d) > 0.0001d;
    }

    private static bool HasActiveScoreModifiers(BeatmapRunState runState)
    {
        return BeatLeaderModifierPolicy.GetScoringModifiers(
            runState.ActiveModifiers,
            includeNoFailPenalty: runState.IsFailedWithNoFail).Count > 0;
    }

    private static int? ResolveKnownMaxModifiedScore(BeatmapRunState runState, LeaderboardSessionCache cache)
    {
        return runState.KnownMaxModifiedScore;
    }

    private static int ComputeBeatLeaderUnrankedClassicScore(BeatmapRunState runState, LeaderboardSessionCache cache, double accuracy)
    {
        var maxScore = Math.Max(0, cache.MaxScore.GetValueOrDefault());
        if (maxScore <= 0)
        {
            return ResolveCurrentRankingScore(runState, cache);
        }

        var projectedBaseScore = (int)Math.Round(maxScore * Clamp01(accuracy), MidpointRounding.AwayFromZero);
        if (!HasActiveScoreModifiers(runState) || Math.Abs(cache.ScoreModifierMultiplier - 1d) <= 0.0001d)
        {
            return Math.Max(0, projectedBaseScore);
        }

        var positiveMultiplier = Math.Max(1d, cache.PositiveScoreModifierMultiplier);
        var negativeMultiplier = Math.Max(0d, cache.NegativeScoreModifierMultiplier);
        var positiveBonusTarget = (int)((float)Math.Max(0, maxScore - projectedBaseScore) * (float)(positiveMultiplier - 1d));
        var modifiedScore = (int)((projectedBaseScore + positiveBonusTarget) * negativeMultiplier);
        return Math.Max(0, modifiedScore);
    }

    private static bool IsBetterOrEqual(int projectedFinalScore, double? rankingPp, double rankingAccuracy, BeatLeader.BeatLeaderScoreRow row, bool usePpRanking, bool useAccuracyRanking)
    {
        if (useAccuracyRanking)
        {
            var rowAccuracy = row.Accuracy.GetValueOrDefault();
            if (rowAccuracy > 1.0001d)
            {
                rowAccuracy /= 100d;
            }

            return rankingAccuracy >= rowAccuracy;
        }

        if (!usePpRanking)
        {
            return projectedFinalScore >= row.ModifiedScore;
        }

        var rowPp = row.Pp.GetValueOrDefault();
        if (rowPp <= 0)
        {
            return projectedFinalScore >= row.ModifiedScore;
        }

        return rankingPp.GetValueOrDefault() >= rowPp;
    }

    private static double? EstimateRankedProjectedPp(
        BeatmapRunState runState,
        LeaderboardSessionCache cache,
        int rankingScore,
        int projectedFinalScore,
        System.Collections.Generic.IReadOnlyList<BeatLeader.BeatLeaderScoreRow> rows)
    {
        if (cache.HasBeatLeaderRatings)
        {
            return EstimateBeatLeaderCurvePp(runState, cache, projectedFinalScore);
        }

        var starRating = cache.StarRating.GetValueOrDefault();
        if (cache.UsesScoreSaberPpCurve && starRating > 0d)
        {
            var effectiveAccuracy = ResolveRankingAccuracy(runState, cache, projectedFinalScore);
            return ScoreSaberPpCalculator.Calculate(starRating, effectiveAccuracy);
        }

        return EstimateProjectedPpFromScore(rankingScore, rows);
    }

    private static double? EstimateProjectedPpFromScore(int projectedFinalScore, System.Collections.Generic.IReadOnlyList<BeatLeader.BeatLeaderScoreRow> rows)
    {
        var ppRows = rows
            .Where(row => row.Pp.HasValue && row.Pp.Value > 0)
            .OrderByDescending(row => row.ModifiedScore)
            .ToList();

        if (ppRows.Count == 0)
        {
            return null;
        }

        if (ppRows.Count == 1)
        {
            return Math.Max(0, ppRows[0].Pp!.Value * Clamp01((projectedFinalScore + 1d) / Math.Max(1d, ppRows[0].ModifiedScore)));
        }

        if (projectedFinalScore >= ppRows[0].ModifiedScore)
        {
            return Extrapolate(projectedFinalScore, ppRows[0], ppRows[1]);
        }

        var lastIndex = ppRows.Count - 1;
        if (projectedFinalScore <= ppRows[lastIndex].ModifiedScore)
        {
            return Extrapolate(projectedFinalScore, ppRows[lastIndex - 1], ppRows[lastIndex]);
        }

        for (var i = 0; i < ppRows.Count - 1; i++)
        {
            var high = ppRows[i];
            var low = ppRows[i + 1];
            if (projectedFinalScore <= high.ModifiedScore && projectedFinalScore >= low.ModifiedScore)
            {
                var denominator = high.ModifiedScore - low.ModifiedScore;
                if (denominator <= 0)
                {
                    return Math.Max(0, low.Pp!.Value);
                }

                var t = (double)(projectedFinalScore - low.ModifiedScore) / denominator;
                return Math.Max(0, low.Pp!.Value + ((high.Pp!.Value - low.Pp!.Value) * t));
            }
        }

        return null;
    }

    private static double? EstimateBeatLeaderCurvePp(BeatmapRunState runState, LeaderboardSessionCache cache, int projectedFinalScore)
    {
        var modifierMultiplier = cache.ModifierMultiplier <= 0 ? 0d : cache.ModifierMultiplier;
        var passRating = cache.PassRating.GetValueOrDefault() * modifierMultiplier;
        var accRating = cache.AccRating.GetValueOrDefault() * modifierMultiplier;
        var techRating = cache.TechRating.GetValueOrDefault() * modifierMultiplier;
        if (passRating <= 0 && accRating <= 0 && techRating <= 0)
        {
            return null;
        }

        var percentage = ResolveProjectedAccuracyPercent(runState, cache, projectedFinalScore);
        if (percentage <= 0 && runState.CurrentModifiedScore > 0)
        {
            percentage = NormalizePredictedAccuracyPercent(cache.PredictedAcc.GetValueOrDefault(95d));
        }

        return CalculateBeatLeaderPpAtPercentage(percentage, accRating, passRating, techRating, failed: runState.IsFailedWithNoFail, paused: false);
    }

    private static double ResolveProjectedAccuracyPercent(BeatmapRunState runState, LeaderboardSessionCache cache, int projectedFinalScore)
    {
        var knownMax = ResolveKnownMaxModifiedScore(runState, cache).GetValueOrDefault();
        if (knownMax > 0 && projectedFinalScore > 0)
        {
            return Math.Max(0d, Math.Min(100d, ((double)projectedFinalScore / knownMax) * 100d));
        }

        return Clamp01(runState.Accuracy) * 100d;
    }

    // Mirrors PPPredictor.Core DataType.Curve.BeatLeaderPPPCurve.CalculatePPatPercentage.
    private static double CalculateBeatLeaderPpAtPercentage(double percentage, double accRating, double passRating, double techRating, bool failed, bool paused)
    {
        if (failed || paused)
        {
            return 0;
        }

        var accuracy = Math.Max(0d, Math.Min(100d, percentage)) / 100d;
        var passPp = (15.2 * Math.Exp(Math.Pow(passRating, 1d / 2.62d))) - 30d;
        if (double.IsNaN(passPp) || double.IsInfinity(passPp) || passPp < 0)
        {
            passPp = 0;
        }

        var accPp = BeatLeaderAccCurveValue(accuracy) * accRating * 34d;
        var techPp = Math.Exp(1.9d * accuracy) * 1.08d * techRating;
        var rawPp = InflateBeatLeaderPp(passPp + accPp + techPp);
        return rawPp <= 0 || double.IsNaN(rawPp) || double.IsInfinity(rawPp) ? 0 : rawPp;
    }

    private static double NormalizePredictedAccuracyPercent(double predictedAcc)
    {
        if (predictedAcc <= 0)
        {
            return 95d;
        }

        return predictedAcc <= 1.0001d ? predictedAcc * 100d : predictedAcc;
    }

    private static double BeatLeaderAccCurveValue(double accuracy)
    {
        var clamped = Clamp01(accuracy);
        for (var i = 1; i < BeatLeaderAccCurve.Length; i++)
        {
            var previous = BeatLeaderAccCurve[i - 1];
            var current = BeatLeaderAccCurve[i];
            if (clamped > previous.Accuracy || clamped < current.Accuracy)
            {
                continue;
            }

            var denominator = previous.Accuracy - current.Accuracy;
            if (denominator <= 0)
            {
                return current.Value;
            }

            var t = (clamped - current.Accuracy) / denominator;
            return current.Value + ((previous.Value - current.Value) * t);
        }

        return BeatLeaderAccCurve[BeatLeaderAccCurve.Length - 1].Value;
    }

    private static double InflateBeatLeaderPp(double pp)
    {
        if (pp <= 0)
        {
            return 0;
        }

        const double scale = 650d;
        return (scale * Math.Pow(pp, 1.3d)) / Math.Pow(scale, 1.3d);
    }

    private static double Extrapolate(int projectedFinalScore, BeatLeader.BeatLeaderScoreRow a, BeatLeader.BeatLeaderScoreRow b)
    {
        var denominator = a.ModifiedScore - b.ModifiedScore;
        if (denominator == 0)
        {
            return Math.Max(0, a.Pp!.Value);
        }

        var slope = (a.Pp!.Value - b.Pp!.Value) / denominator;
        return Math.Max(0, a.Pp.Value + ((projectedFinalScore - a.ModifiedScore) * slope));
    }

    private static double Clamp01(double value)
    {
        if (value < 0)
        {
            return 0;
        }

        if (value > 1)
        {
            return 1;
        }

        return value;
    }
}
