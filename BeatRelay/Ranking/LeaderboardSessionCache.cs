using System;
using System.Collections.Generic;
using System.Linq;
using BeatRelay.BeatLeader;

namespace BeatRelay.Ranking;

public sealed class LeaderboardSessionCache
{
    private readonly Dictionary<int, BeatLeaderScoreRow> scoresByRank = new();
    private readonly HashSet<int> fetchedPages = new();

    public LeaderboardSessionCache(string hash, string difficulty, string mode)
    {
        Hash = Require(hash, nameof(hash)).ToLowerInvariant();
        Difficulty = Require(difficulty, nameof(difficulty));
        Mode = Require(mode, nameof(mode));
    }

    public string Hash { get; }

    public string Difficulty { get; }

    public string Mode { get; }

    public string LeaderboardId { get; private set; } = string.Empty;

    public string SourceName { get; private set; } = "BeatLeader";

    public bool Ranked { get; private set; }

    public bool RankByPp { get; private set; } = true;

    public bool SupportsPp { get; private set; } = true;

    public bool UsesScoreSaberPpCurve { get; private set; }

    public bool PositiveModifiers { get; private set; }

    public string LeaderboardStatus { get; private set; } = string.Empty;

    public int? RealmId { get; private set; }

    public string RealmName { get; private set; } = string.Empty;

    public double? StarRating { get; private set; }

    public int? MaxScore { get; private set; }

    public int? EffectiveMaxScore
    {
        get
        {
            if (!MaxScore.HasValue)
            {
                return null;
            }

            var multiplier = ScoreModifierMultiplier <= 0 ? 0d : ScoreModifierMultiplier;
            if (Math.Abs(multiplier - 1d) <= 0.0001d)
            {
                return MaxScore.Value;
            }

            return Math.Max(0, (int)Math.Round(MaxScore.Value * multiplier, MidpointRounding.AwayFromZero));
        }
    }

    public int TotalScores { get; private set; }

    public int ItemsPerPage { get; private set; }

    public double? PredictedAcc { get; private set; }

    public double? PassRating { get; private set; }

    public double? AccRating { get; private set; }

    public double? TechRating { get; private set; }

    public double ModifierMultiplier { get; private set; } = 1d;

    public double ScoreModifierMultiplier { get; private set; } = 1d;

    public double PositiveScoreModifierMultiplier { get; private set; } = 1d;

    public double NegativeScoreModifierMultiplier { get; private set; } = 1d;

    public IReadOnlyDictionary<int, BeatLeaderScoreRow> ScoresByRank => scoresByRank;

    public IReadOnlyCollection<int> FetchedPages => fetchedPages;

    public bool HasUsableRows => scoresByRank.Count > 0;

    public bool HasBeatLeaderRatings =>
        Ranked &&
        (PassRating.GetValueOrDefault() > 0 || AccRating.GetValueOrDefault() > 0 || TechRating.GetValueOrDefault() > 0);

    public void ApplyPage(LeaderboardScoresResponse response)
    {
        ApplyResponse(response, markFetchedPage: true, updateItemsPerPage: true);
    }

    public void ApplyScannedPage(LeaderboardScoresResponse response)
    {
        ApplyResponse(response, markFetchedPage: false, updateItemsPerPage: false);
    }

    private void ApplyResponse(LeaderboardScoresResponse response, bool markFetchedPage, bool updateItemsPerPage)
    {
        if (response.Metadata == null)
        {
            throw new ArgumentException("Leaderboard response is missing metadata.", nameof(response));
        }

        if (response.Container == null || string.IsNullOrWhiteSpace(response.Container.LeaderboardId))
        {
            throw new ArgumentException("Leaderboard response is missing a leaderboard ID.", nameof(response));
        }

        LeaderboardId = response.Container.LeaderboardId!;
        SourceName = string.IsNullOrWhiteSpace(response.Container.SourceName) ? "BeatLeader" : response.Container.SourceName.Trim();
        Ranked = response.Container.Ranked;
        RankByPp = response.Container.RankByPp.GetValueOrDefault(Ranked);
        SupportsPp = response.Container.SupportsPp.GetValueOrDefault(Ranked);
        UsesScoreSaberPpCurve = response.Container.UsesScoreSaberPpCurve;
        PositiveModifiers = response.Container.PositiveModifiers;
        LeaderboardStatus = response.Container.LeaderboardStatus ?? string.Empty;
        RealmId = response.Container.RealmId;
        RealmName = response.Container.RealmName ?? string.Empty;
        StarRating = response.Container.ResolveStars();
        MaxScore = response.Container.MaxScore ?? MaxScore ?? TryInferMaxScore(response);
        TotalScores = Math.Max(0, response.Metadata.Total);
        if (updateItemsPerPage || ItemsPerPage <= 0)
        {
            ItemsPerPage = Math.Max(1, response.Metadata.ItemsPerPage);
        }

        if (markFetchedPage)
        {
            fetchedPages.Add(response.Metadata.Page);
        }

        foreach (var row in response.ToScoreRows())
        {
            scoresByRank[row.Rank] = row;
        }

        RefreshBeatLeaderRankedFromScoreEvidence();
    }

    public void ApplyScore(BeatLeaderScoreDto score)
    {
        if (score == null)
        {
            throw new ArgumentNullException(nameof(score));
        }

        if (score.Rank <= 0 || score.ModifiedScore <= 0)
        {
            return;
        }

        scoresByRank[score.Rank] = score.ToScoreRow();
        RefreshBeatLeaderRankedFromScoreEvidence();
    }

    private void RefreshBeatLeaderRankedFromScoreEvidence()
    {
        if (Ranked || string.Equals(SourceName, "ScoreSaber", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (scoresByRank.Values.Any(row => row.Pp.GetValueOrDefault() > 0d))
        {
            Ranked = true;
            RankByPp = true;
            SupportsPp = true;
        }
    }

    private static int? TryInferMaxScore(LeaderboardScoresResponse response)
    {
        if (response.Data == null || response.Data.Count == 0)
        {
            return null;
        }

        var candidates = new List<int>();
        foreach (var score in response.Data)
        {
            var baseScore = score.BaseScore.GetValueOrDefault();
            var accuracy = score.Accuracy.GetValueOrDefault();
            if (baseScore <= 0 || accuracy <= 0 || accuracy > 1.1d)
            {
                continue;
            }

            candidates.Add((int)Math.Round(baseScore / accuracy, MidpointRounding.AwayFromZero));
        }

        if (candidates.Count == 0)
        {
            return null;
        }

        return Math.Max(0, (int)Math.Round(candidates.Average(), MidpointRounding.AwayFromZero));
    }

    public void ApplyMaxScore(int? maxScore)
    {
        if (maxScore.HasValue && maxScore.Value > 0)
        {
            MaxScore = maxScore.Value;
        }
    }

    public void ApplyDifficultyRatings(
        double? stars,
        double? predictedAcc,
        double? passRating,
        double? accRating,
        double? techRating,
        double modifierMultiplier = 1d,
        double? scoreModifierMultiplier = null,
        double? positiveScoreModifierMultiplier = null,
        double? negativeScoreModifierMultiplier = null)
    {
        if (stars.HasValue && stars.Value > 0)
        {
            StarRating = stars;
        }

        PredictedAcc = predictedAcc;
        PassRating = passRating;
        AccRating = accRating;
        TechRating = techRating;
        ModifierMultiplier = modifierMultiplier <= 0 ? 0d : modifierMultiplier;
        var scoreMultiplier = scoreModifierMultiplier ?? modifierMultiplier;
        ScoreModifierMultiplier = scoreMultiplier <= 0 ? 0d : scoreMultiplier;
        PositiveScoreModifierMultiplier = positiveScoreModifierMultiplier.GetValueOrDefault(ScoreModifierMultiplier >= 1d ? ScoreModifierMultiplier : 1d);
        NegativeScoreModifierMultiplier = negativeScoreModifierMultiplier.GetValueOrDefault(ScoreModifierMultiplier < 1d ? ScoreModifierMultiplier : 1d);
    }

    public void ApplyScoreModifierMultipliers(double positiveScoreModifierMultiplier, double negativeScoreModifierMultiplier)
    {
        PositiveScoreModifierMultiplier = positiveScoreModifierMultiplier <= 0 ? 1d : positiveScoreModifierMultiplier;
        NegativeScoreModifierMultiplier = negativeScoreModifierMultiplier <= 0 ? 0d : negativeScoreModifierMultiplier;
        ScoreModifierMultiplier = Math.Max(0d, PositiveScoreModifierMultiplier * NegativeScoreModifierMultiplier);
    }

    public bool HasCoverageAroundRank(int rank, int radius)
    {
        if (rank <= 0 || radius < 0)
        {
            return false;
        }

        var lower = Math.Max(1, rank - radius);
        var upper = Math.Min(Math.Max(TotalScores, rank + radius), rank + radius);
        for (var current = lower; current <= upper; current++)
        {
            if (!scoresByRank.ContainsKey(current))
            {
                return false;
            }
        }

        return true;
    }

    public int GetPageForRank(int rank)
    {
        if (ItemsPerPage <= 0 || rank <= 0)
        {
            return 1;
        }

        return ((rank - 1) / ItemsPerPage) + 1;
    }

    public IReadOnlyList<BeatLeaderScoreRow> GetNearbyRows(int rank, int radius)
    {
        var rows = new List<BeatLeaderScoreRow>();
        if (rank <= 0)
        {
            return rows;
        }

        var lower = Math.Max(1, rank - radius);
        var upper = rank + radius;
        for (var current = lower; current <= upper; current++)
        {
            if (scoresByRank.TryGetValue(current, out var row))
            {
                rows.Add(row);
            }
        }

        return rows;
    }

    public int? FindFirstMissingPageInRange(int lowerRank, int upperRank, bool reverse)
    {
        if (ItemsPerPage <= 0)
        {
            return null;
        }

        var normalizedLower = Math.Max(1, lowerRank);
        var normalizedUpper = Math.Max(normalizedLower, upperRank);
        var startPage = GetPageForRank(normalizedLower);
        var endPage = GetPageForRank(normalizedUpper);
        if (startPage > endPage)
        {
            return null;
        }

        if (!reverse)
        {
            for (var page = startPage; page <= endPage; page++)
            {
                if (!fetchedPages.Contains(page) && !HasFetchedPageRankCoverage(page))
                {
                    return page;
                }
            }

            return null;
        }

        for (var page = endPage; page >= startPage; page--)
        {
            if (!fetchedPages.Contains(page) && !HasFetchedPageRankCoverage(page))
            {
                return page;
            }
        }

        return null;
    }

    private bool HasFetchedPageRankCoverage(int page)
    {
        if (ItemsPerPage <= 0 || page <= 0)
        {
            return false;
        }

        var lowerRank = ((page - 1) * ItemsPerPage) + 1;
        var upperRank = page * ItemsPerPage;
        if (TotalScores > 0)
        {
            upperRank = Math.Min(upperRank, TotalScores);
        }

        for (var rank = lowerRank; rank <= upperRank; rank++)
        {
            if (!scoresByRank.ContainsKey(rank))
            {
                return false;
            }
        }

        return true;
    }

    private static string Require(string value, string paramName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Value is required.", paramName);
        }

        return value.Trim();
    }
}
