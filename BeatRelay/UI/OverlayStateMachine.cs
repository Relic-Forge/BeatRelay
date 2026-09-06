using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using BeatRelay.BeatLeader;
using BeatRelay.BeatSaber;
using BeatRelay.Config;
using BeatRelay.Ranking;
using BeatRelay.ScoreSaber;

namespace BeatRelay.UI;

public sealed class OverlayStateMachine
{
    public const string PlayerLabel = "You";
    private readonly OverlayConfig config;
    private readonly ProjectionEngine projectionEngine;
    private readonly ConfidenceEngine confidenceEngine;
    private readonly RankSmoother rankSmoother;
    private const int FetchBoundaryBufferRanks = 1;

    private LeaderboardSessionCache? cache;
    private string localPlayerId = string.Empty;
    private IReadOnlyList<OverlayRowViewModel> lastRows = new List<OverlayRowViewModel>();
    private readonly Dictionary<int, OverlayRowViewModel> rememberedOpponentRowsByRank = new();
    private int? lastAnimatedRank;
    private bool runtimeAlwaysExpand;

    public OverlayStateMachine(OverlayConfig config)
        : this(config, new ProjectionEngine(), new ConfidenceEngine(), new RankSmoother())
    {
    }

    public OverlayStateMachine(
        OverlayConfig config,
        ProjectionEngine projectionEngine,
        ConfidenceEngine confidenceEngine,
        RankSmoother rankSmoother)
    {
        this.config = config ?? throw new ArgumentNullException(nameof(config));
        this.projectionEngine = projectionEngine ?? throw new ArgumentNullException(nameof(projectionEngine));
        this.confidenceEngine = confidenceEngine ?? throw new ArgumentNullException(nameof(confidenceEngine));
        this.rankSmoother = rankSmoother ?? throw new ArgumentNullException(nameof(rankSmoother));
    }

    public bool IsActive => cache != null;

    public void SetRuntimeAlwaysExpand(bool enabled)
    {
        runtimeAlwaysExpand = enabled;
    }

    public void StartSession(LeaderboardSessionCache sessionCache, string? playerId)
    {
        cache = sessionCache ?? throw new ArgumentNullException(nameof(sessionCache));
        localPlayerId = string.IsNullOrWhiteSpace(playerId) ? string.Empty : playerId!.Trim();

        ResetSessionAnimationState();
    }

    public void StartReplaySession(LeaderboardSessionCache sessionCache)
    {
        cache = sessionCache ?? throw new ArgumentNullException(nameof(sessionCache));
        localPlayerId = string.Empty;

        ResetSessionAnimationState();
    }

    private void ResetSessionAnimationState()
    {
        confidenceEngine.Reset();
        rankSmoother.Reset();
        lastRows = new List<OverlayRowViewModel>();
        rememberedOpponentRowsByRank.Clear();
        lastAnimatedRank = null;
    }

    public void EndSession()
    {
        cache = null;
        localPlayerId = string.Empty;
        confidenceEngine.Reset();
        rankSmoother.Reset();
        lastRows = new List<OverlayRowViewModel>();
        rememberedOpponentRowsByRank.Clear();
        lastAnimatedRank = null;
    }

    public OverlayUpdateResult Update(BeatmapRunState runState)
    {
        // A replay detected after live startup must not retain the viewer's row identity.
        if (runState.IsReplayMode)
        {
            localPlayerId = string.Empty;
        }

        if (!config.Enabled)
        {
            return new OverlayUpdateResult(OverlayViewModel.Hidden(), null);
        }

        if (cache == null)
        {
            return new OverlayUpdateResult(OverlayViewModel.Hidden(), null);
        }

        if (!cache.HasUsableRows)
        {
            return new OverlayUpdateResult(OverlayViewModel.Unavailable($"{cache.SourceName} leaderboard unavailable for this map"), null);
        }

        if (cache.EffectiveMaxScore.HasValue && !runState.KnownMaxModifiedScore.HasValue)
        {
            runState.KnownMaxModifiedScore = cache.EffectiveMaxScore.Value;
        }

        var ppRankingEnabled = ShouldUsePpRankingMode();
        var projection = projectionEngine.Project(runState, cache, ppRankingEnabled);
        var confidence = confidenceEngine.Evaluate(runState, projection);
        var mode = ResolveMode(runState);
        var pageFetch = ResolvePageToFetch(projection, runState);

        var rankText = "--";
        var animateRankChange = false;
        if (projection.ProjectedRank.HasValue)
        {
            var smoothed = rankSmoother.Update(projection.ProjectedRank.Value, runState.SongProgressRatio);
            rankText = confidence.CanShowNumericRank
                ? $"#{smoothed.Rank}"
                : (string.IsNullOrWhiteSpace(confidence.DisplayText) ? "--" : confidence.DisplayText);
            if (lastAnimatedRank.HasValue)
            {
                animateRankChange = smoothed.ShowMovement && Math.Abs(smoothed.Rank - lastAnimatedRank.Value) > 5;
            }

            lastAnimatedRank = smoothed.Rank;
        }
        else if (!string.IsNullOrWhiteSpace(confidence.DisplayText))
        {
            rankText = confidence.DisplayText;
        }

        var rows = BuildRows(mode, projection, ppRankingEnabled, runState);
        if (rows.Count > 0)
        {
            lastRows = rows;
        }
        else if (lastRows.Count > 0)
        {
            rows = lastRows;
        }

        var displayScore = ResolveDisplayedProjectedScore(projection, runState);
        var viewModel = new OverlayViewModel
        {
            Mode = mode,
            DisplayName = PlayerLabel,
            RankText = rankText,
            MovementText = string.Empty,
            MessageText = string.Empty,
            MapContextText = BuildMapContextText(cache),
            ModifiersText = BuildModifiersText(runState.ActiveModifiers, runState.IsFailedWithNoFail),
            SourceName = cache.SourceName,
            IsRanked = cache.Ranked,
            SupportsPp = cache.SupportsPp,
            ProjectedScore = displayScore,
            AnimateRankChange = animateRankChange,
            NoFailPenaltyActive = runState.IsFailedWithNoFail,
            Rows = rows
        };

        return new OverlayUpdateResult(viewModel, pageFetch.Page, pageFetch.ForceRefresh, projection);
    }

    private OverlayMode ResolveMode(BeatmapRunState runState)
    {
        if (runtimeAlwaysExpand || runState.IsReplayMode || runState.IsPaused || config.AlwaysExpand)
        {
            return OverlayMode.Expanded;
        }

        var shouldExpandNow = ShouldExpandPredictively(runState);
        return shouldExpandNow ? OverlayMode.Expanded : OverlayMode.Collapsed;
    }

    private (int? Page, bool ForceRefresh) ResolvePageToFetch(ProjectionResult projection, BeatmapRunState runState)
    {
        if (cache == null || !projection.ProjectedRank.HasValue)
        {
            return (null, false);
        }

        var scoreSaberMode = string.Equals(cache.SourceName, "ScoreSaber", StringComparison.OrdinalIgnoreCase);
        if (!scoreSaberMode
            && !cache.Ranked
            && cache.TotalScores > 0
            && cache.FetchedPages.Count == 1
            && cache.FetchedPages.Contains(1))
        {
            var bottomPage = cache.GetPageForRank(cache.TotalScores);
            if (bottomPage > 1 && !cache.FetchedPages.Contains(bottomPage))
            {
                return (bottomPage, false);
            }
        }

        var projectedRank = projection.ProjectedRank.Value;
        var targetRows = Math.Max(0, config.VisiblePlayerCount);
        if (targetRows == 0)
        {
            return (null, false);
        }

        var selectedRanks = BuildRankSelection(projectedRank, targetRows);
        if (selectedRanks.Count == 0)
        {
            return (null, false);
        }

        var missingVisiblePage = ResolveMissingVisibleRankPage(selectedRanks, projectedRank);
        if (missingVisiblePage.HasValue && cache.FetchedPages.Contains(missingVisiblePage.Value))
        {
            return (missingVisiblePage.Value, true);
        }

        var lowerRank = Math.Max(1, selectedRanks.Min() - FetchBoundaryBufferRanks);
        var upperRank = Math.Max(lowerRank, selectedRanks.Max() + FetchBoundaryBufferRanks);
        if (cache.TotalScores > 0)
        {
            upperRank = Math.Min(upperRank, cache.TotalScores);
        }

        var missingPage = cache.Ranked || scoreSaberMode
            ? cache.FindFirstMissingPageInRange(lowerRank, upperRank, reverse: false)
            : cache.FindFirstMissingPageInRange(lowerRank, upperRank, reverse: true);
        if (missingPage.HasValue)
        {
            return (missingPage, false);
        }

        return (null, false);
    }

    private int? ResolveMissingVisibleRankPage(IReadOnlyList<int> selectedRanks, int projectedRank)
    {
        if (cache == null || cache.ItemsPerPage <= 0)
        {
            return null;
        }

        foreach (var rank in selectedRanks.OrderBy(rank => rank))
        {
            if (rank == projectedRank || rank <= 0 || cache.ScoresByRank.ContainsKey(rank))
            {
                continue;
            }

            var page = cache.GetPageForRank(rank);
            if (page >= 1)
            {
                return page;
            }
        }

        return null;
    }

    private IReadOnlyList<OverlayRowViewModel> BuildRows(OverlayMode mode, ProjectionResult projection, bool ppRankingEnabled, BeatmapRunState runState)
    {
        if (cache == null || !projection.ProjectedRank.HasValue)
        {
            return new List<OverlayRowViewModel>();
        }

        var targetRows = Math.Max(0, config.VisiblePlayerCount);
        if (targetRows == 0)
        {
            return new List<OverlayRowViewModel>();
        }

        var radius = Math.Max(3, targetRows + 2);
        var projectedRank = projection.ProjectedRank.Value;
        var selectedRanks = BuildRankSelection(projectedRank, targetRows);
        var lowerRank = selectedRanks.Min();
        var upperRank = selectedRanks.Max();

        var displayScore = ResolveDisplayedProjectedScore(projection, runState);
        var rows = cache.GetNearbyRows(projectedRank, radius + 1)
            .Select(row => ToRowViewModel(row, row.Rank >= projectedRank ? row.Rank + 1 : row.Rank))
            .GroupBy(row => BuildIdentityKey(row))
            .Select(group => group.First())
            .Where(row => selectedRanks.Contains(row.Rank))
            .ToList();
        rows.RemoveAll(row => !string.IsNullOrWhiteSpace(localPlayerId)
            && string.Equals(row.PlayerId, localPlayerId, StringComparison.OrdinalIgnoreCase));
        rows.Add(new OverlayRowViewModel
        {
            Rank = projectedRank,
            PlayerId = localPlayerId,
            PlayerName = PlayerLabel,
            ValueText = FormatProjectedValue(projection, ppRankingEnabled, displayScore),
            Modifiers = BuildLocalRowModifiers(runState),
            Accuracy = projection.LiveAccuracy,
            Score = displayScore,
            IsLocalPlayer = true,
            IsProjected = true
        });

        rows = rows
            .OrderBy(row => row.Rank)
            .ThenByDescending(row => row.IsLocalPlayer)
            .ToList();

        var deduped = new List<OverlayRowViewModel>(rows.Count);
        var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            var key = BuildIdentityKey(row);
            if (!seenKeys.Add(key))
            {
                continue;
            }

            deduped.Add(row);
        }

        deduped = FillMissingRanks(deduped, selectedRanks, projectedRank, projection, ppRankingEnabled, displayScore);

        RememberResolvedOpponentRows(deduped);

        return deduped
            .Where(row => selectedRanks.Contains(row.Rank))
            .OrderBy(row => row.Rank)
            .Take(targetRows)
            .ToList();
    }

    private OverlayRowViewModel ToRowViewModel(BeatLeaderScoreRow row, int displayRank)
    {
        return new OverlayRowViewModel
        {
            Rank = displayRank,
            PlayerId = row.PlayerId,
            PlayerName = NormalizeDisplayName(row.PlayerName) ?? "Unknown Player",
            ValueText = FormatValue(row),
            Modifiers = row.Modifiers,
            Accuracy = row.Accuracy,
            Score = row.ModifiedScore > 0 ? row.ModifiedScore : null,
            IsLocalPlayer = false,
            IsProjected = false
        };
    }

    private string FormatValue(BeatLeaderScoreRow row)
    {
        if (IsBeatLeaderUnrankedCache())
        {
            return row.ModifiedScore > 0
                ? row.ModifiedScore.ToString("N0", CultureInfo.InvariantCulture)
                : string.Empty;
        }

        if (cache?.SupportsPp == true)
        {
            return row.Pp.HasValue && row.Pp.Value > 0
                ? row.Pp.Value.ToString("0.0", CultureInfo.InvariantCulture) + "pp"
                : "--";
        }

        if (row.ModifiedScore > 0)
        {
            return row.ModifiedScore.ToString("N0", CultureInfo.InvariantCulture);
        }

        return "--";
    }

    private string FormatProjectedValue(ProjectionResult projection, bool ppRankingEnabled, int? displayScore)
    {
        if (IsBeatLeaderUnrankedCache())
        {
            return displayScore.HasValue && displayScore.Value > 0
                ? displayScore.Value.ToString("N0", CultureInfo.InvariantCulture)
                : string.Empty;
        }

        if (cache?.SupportsPp == true)
        {
            var effectivePp = projection.RankingPp ?? projection.ProjectedPp;
            return effectivePp.HasValue && effectivePp.Value > 0
                ? effectivePp.Value.ToString("0.0", CultureInfo.InvariantCulture) + "pp"
                : "--";
        }

        if (displayScore.HasValue && displayScore.Value > 0)
        {
            return displayScore.Value.ToString("N0", CultureInfo.InvariantCulture);
        }

        return IsBeatLeaderUnrankedCache() ? string.Empty : "--";
    }

    private int? ResolveDisplayedProjectedScore(ProjectionResult projection, BeatmapRunState runState)
    {
        if (IsBeatLeaderUnrankedCache())
        {
            return runState.CurrentModifiedScore > 0 ? runState.CurrentModifiedScore : null;
        }

        return projection.ProjectedFinalScore;
    }

    private bool IsBeatLeaderUnrankedCache()
    {
        return cache != null
            && !cache.Ranked
            && !string.Equals(cache.SourceName, "ScoreSaber", StringComparison.OrdinalIgnoreCase);
    }

    private string BuildLocalRowModifiers(BeatmapRunState runState)
    {
        if (cache == null || !string.Equals(cache.SourceName, "ScoreSaber", StringComparison.OrdinalIgnoreCase))
        {
            return string.Join(",", BeatLeaderModifierPolicy.GetDisplayModifiers(runState.ActiveModifiers, runState.IsFailedWithNoFail));
        }

        return string.Join(",", ScoreSaberModifierPolicy.GetDisplayModifiers(runState.ActiveModifiers, runState.IsFailedWithNoFail));
    }

    private List<int> BuildRankSelection(int projectedRank, int targetRows)
    {
        var knownMaxRank = Math.Max(projectedRank, cache?.TotalScores ?? projectedRank);
        var selected = new List<int> { projectedRank };
        if (targetRows <= 1)
        {
            return selected;
        }

        var step = 1;
        var useAbove = true;
        while (selected.Count < targetRows)
        {
            if (useAbove)
            {
                var above = projectedRank - step;
                if (above >= 1)
                {
                    selected.Add(above);
                }
                else
                {
                    var fallbackBelow = projectedRank + step;
                    if (fallbackBelow <= knownMaxRank)
                    {
                        selected.Add(fallbackBelow);
                    }
                }
                useAbove = false;
            }
            else
            {
                var below = projectedRank + step;
                if (below <= knownMaxRank)
                {
                    selected.Add(below);
                }
                else
                {
                    var fallbackAbove = projectedRank - step;
                    if (fallbackAbove >= 1)
                    {
                        selected.Add(fallbackAbove);
                    }
                }
                useAbove = true;
                step++;
            }

            if (selected.Count >= targetRows)
            {
                break;
            }

            if (step > 10000)
            {
                break;
            }
        }

        return selected;
    }

    private static string? NormalizeDisplayName(string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return string.Empty;
        }

        var trimmed = candidate.Trim();
        var upper = trimmed.ToUpperInvariant();
        if (upper.IndexOf("NO NAME", StringComparison.Ordinal) >= 0
            || string.Equals(trimmed, "Unknown Player", StringComparison.OrdinalIgnoreCase)
            || string.Equals(trimmed, "Unknown", StringComparison.OrdinalIgnoreCase)
            || string.Equals(trimmed, "N/A", StringComparison.OrdinalIgnoreCase)
            || string.Equals(trimmed, "-", StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        return trimmed;
    }

    private static string BuildIdentityKey(OverlayRowViewModel row)
    {
        if (!string.IsNullOrWhiteSpace(row.PlayerId))
        {
            return "id:" + row.PlayerId.Trim().ToLowerInvariant();
        }

        return "name:" + row.PlayerName.Trim().ToLowerInvariant();
    }

    private static string BuildMapContextText(LeaderboardSessionCache cache)
    {
        if (!cache.Ranked)
        {
            return cache.Difficulty;
        }

        if (cache.StarRating.HasValue)
        {
            return $"{cache.Difficulty} / {cache.StarRating.Value.ToString("0.00", CultureInfo.InvariantCulture)}*";
        }

        return cache.Difficulty;
    }

    private static string BuildModifiersText(IReadOnlyList<string> modifiers, bool includeNoFail)
    {
        var displayModifiers = BeatLeaderModifierPolicy.GetDisplayModifiers(modifiers, includeNoFail);
        if (displayModifiers.Count == 0)
        {
            return string.Empty;
        }

        return "Mods: " + string.Join(", ", displayModifiers);
    }

    private bool ShouldUsePpRankingMode()
    {
        if (cache != null && string.Equals(cache.SourceName, "ScoreSaber", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return cache?.RankByPp == true;
    }

    private bool ShouldExpandPredictively(BeatmapRunState runState)
    {
        if (runState.IsInPlannedBreak)
        {
            return true;
        }

        // Expand immediately after the final scorable block is resolved.
        if (runState.IsAfterLastNote)
        {
            return true;
        }

        // If timeline data says there are no upcoming scorable notes anymore, expand as long
        // as gameplay has actually started to avoid forcing expanded mode at song start.
        if (!runState.HasUpcomingScorableNote && runState.ScoredNotes > 0)
        {
            return true;
        }

        return false;
    }

    private List<OverlayRowViewModel> FillMissingRanks(
        IReadOnlyList<OverlayRowViewModel> rows,
        IReadOnlyList<int> selectedRanks,
        int projectedRank,
        ProjectionResult projection,
        bool ppRankingEnabled,
        int? displayScore)
    {
        var byRank = rows.ToDictionary(row => row.Rank, row => row);
        for (var i = 0; i < selectedRanks.Count; i++)
        {
            var rank = selectedRanks[i];
            if (byRank.ContainsKey(rank))
            {
                continue;
            }

            byRank[rank] = rank == projectedRank
                ? new OverlayRowViewModel
                {
                    Rank = projectedRank,
                    PlayerId = localPlayerId,
                    PlayerName = PlayerLabel,
                    ValueText = FormatProjectedValue(projection, ppRankingEnabled, displayScore),
                    Accuracy = projection.LiveAccuracy,
                    Score = displayScore,
                    IsLocalPlayer = true,
                    IsProjected = true
                }
                : ResolveRememberedOpponentRow(rank) ?? new OverlayRowViewModel
                {
                    Rank = rank,
                    PlayerId = "missing-rank-" + rank.ToString(CultureInfo.InvariantCulture),
                    PlayerName = "Loading...",
                    ValueText = "--",
                    IsLocalPlayer = false,
                    IsProjected = false
                };
        }

        return byRank.Values
            .OrderBy(row => row.Rank)
            .ToList();
    }

    private void RememberResolvedOpponentRows(IEnumerable<OverlayRowViewModel> rows)
    {
        foreach (var row in rows)
        {
            if (row.IsLocalPlayer || row.IsProjected || row.Rank <= 0 || IsLoadingPlaceholder(row))
            {
                continue;
            }

            rememberedOpponentRowsByRank[row.Rank] = CloneRow(row);
        }
    }

    private OverlayRowViewModel? ResolveRememberedOpponentRow(int rank)
    {
        if (!rememberedOpponentRowsByRank.TryGetValue(rank, out var remembered))
        {
            return null;
        }

        return CloneRow(remembered);
    }

    private static OverlayRowViewModel CloneRow(OverlayRowViewModel row)
    {
        return new OverlayRowViewModel
        {
            Rank = row.Rank,
            PlayerId = row.PlayerId,
            PlayerName = row.PlayerName,
            ValueText = row.ValueText,
            Accuracy = row.Accuracy,
            Score = row.Score,
            IsLocalPlayer = row.IsLocalPlayer,
            IsProjected = row.IsProjected
        };
    }

    private static bool IsLoadingPlaceholder(OverlayRowViewModel row)
    {
        return row.PlayerName.Equals("Loading...", StringComparison.OrdinalIgnoreCase)
            || row.PlayerId.StartsWith("missing-rank-", StringComparison.OrdinalIgnoreCase);
    }

}
