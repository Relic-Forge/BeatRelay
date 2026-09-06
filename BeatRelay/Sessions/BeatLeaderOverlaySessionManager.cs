using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BeatRelay.BeatLeader;
using BeatRelay.BeatSaber;
using BeatRelay.Diagnostics;
using BeatRelay.Ranking;
using BeatRelay.ScoreSaber;
using BeatRelay.UI;

namespace BeatRelay.Sessions;

public sealed class BeatLeaderOverlaySessionManager
{
    private const int DefaultPageSize = 10;
    private const int BeatLeaderScanPageSize = 100;
    private const double RankedScanPpGapRatio = 0.04d;
    private const double RankedScanMinPpGap = 3d;
    private const double RankedScanMaxPpGap = 15d;
    private const double ScoreScanCloseEnoughRatio = 0.005d;

    private readonly IBeatLeaderApiClient apiClient;
    private readonly OverlayStateMachine stateMachine;
    private readonly IOverlayLogger logger;
    private readonly object pageFetchSync = new();
    private readonly HashSet<string> pagesInFlight = new();
    private readonly HashSet<int> scannedRankPages = new();
    private readonly Dictionary<string, DateTimeOffset> failedPageRetryAfterUtc = new();

    // All runtime callers enter on Unity's context. Preserve it across I/O so
    // cache commits and gameplay reads are serialized on that same thread.
    private long sessionGeneration;
    private LeaderboardSessionCache? cache;
    private LeaderboardSessionCache? retainedCache;
    private string activeCacheKey = string.Empty;
    private string retainedCacheKey = string.Empty;
    private DifficultyRatingSnapshot? normalDifficultyRatings;
    private DifficultyRatingSnapshot? failedNoFailDifficultyRatings;
    private bool failedNoFailRatingsApplied;

    public IBeatLeaderApiClient ApiClient => apiClient;

    public BeatLeaderOverlaySessionManager(
        IBeatLeaderApiClient apiClient,
        OverlayStateMachine stateMachine,
        IOverlayLogger logger)
    {
        this.apiClient = apiClient ?? throw new ArgumentNullException(nameof(apiClient));
        this.stateMachine = stateMachine ?? throw new ArgumentNullException(nameof(stateMachine));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<OverlaySessionStartResult> StartAsync(
        BeatmapSessionInfo beatmap,
        string? playerId,
        CancellationToken cancellationToken)
    {
        if (beatmap == null)
        {
            throw new ArgumentNullException(nameof(beatmap));
        }

        cancellationToken.ThrowIfCancellationRequested();
        var nextCacheKey = BuildSessionCacheKey(beatmap, apiClient.SourceName);
        End();
        var generation = sessionGeneration;
        normalDifficultyRatings = null;
        failedNoFailDifficultyRatings = null;
        failedNoFailRatingsApplied = false;
        var reusableCache = retainedCache != null
            && string.Equals(retainedCacheKey, nextCacheKey, StringComparison.OrdinalIgnoreCase)
            ? retainedCache
            : null;

        if (reusableCache == null)
        {
            retainedCache = null;
            retainedCacheKey = string.Empty;
        }

        LeaderboardScoresResponse? firstPageValue = null;
        LeaderboardSessionCache sessionCache;
        if (reusableCache != null)
        {
            sessionCache = reusableCache;
            logger.Info("session_cache_reused", $"Reused cached {apiClient.SourceName} leaderboard data for {beatmap.Hash}/{beatmap.Difficulty}/{beatmap.Mode}.");
        }
        else
        {
            var firstPage = await apiClient.GetLeaderboardScoresAsync(
                beatmap.Hash,
                beatmap.Difficulty,
                beatmap.Mode,
                page: 1,
                count: DefaultPageSize,
                cancellationToken).ConfigureAwait(true);

            EnsureCurrentSession(generation, cancellationToken);
            if (!firstPage.IsSuccess || firstPage.Value == null)
            {
                logger.Warn("session_start_failed", firstPage.ErrorMessage ?? $"{apiClient.SourceName} leaderboard unavailable.");
                return OverlaySessionStartResult.NotStarted($"{apiClient.SourceName} leaderboard unavailable for this map");
            }

            firstPageValue = firstPage.Value;

            sessionCache = new LeaderboardSessionCache(beatmap.Hash, beatmap.Difficulty, beatmap.Mode);
            try
            {
                sessionCache.ApplyPage(firstPageValue);
            }
            catch (ArgumentException ex)
            {
                logger.Warn("session_cache_rejected", ex.Message);
                return OverlaySessionStartResult.NotStarted($"{apiClient.SourceName} response was missing required leaderboard data");
            }
        }

        cache = sessionCache;
        activeCacheKey = nextCacheKey;
        retainedCache = null;
        retainedCacheKey = string.Empty;
        if (reusableCache == null && !sessionCache.Ranked && !IsScoreSaberCache(sessionCache))
        {
            await TryLoadUnrankedBottomPageAsync(sessionCache, cancellationToken).ConfigureAwait(true);
        }

        EnsureCurrentSession(generation, cancellationToken);
        await TryLoadDifficultyRatingsAsync(beatmap, sessionCache, cancellationToken).ConfigureAwait(true);
        EnsureCurrentSession(generation, cancellationToken);
        if (firstPageValue != null)
        {
            TryApplyLeaderboardContainerScoreModifiers(firstPageValue.Container, beatmap.ActiveModifiers, sessionCache, includeNoFailPenalty: false);
        }
        TryApplyScoreSaberScoreModifiers(sessionCache, beatmap.ActiveModifiers, includeNoFailPenalty: false);

        var resolvedPlayerId = beatmap.IsReplayMode || string.IsNullOrWhiteSpace(playerId) ? null : playerId!.Trim();
        if (!string.IsNullOrWhiteSpace(resolvedPlayerId))
        {
            await TryLoadPlayerBestPageAsync(beatmap, resolvedPlayerId!, cancellationToken).ConfigureAwait(true);
        }

        EnsureCurrentSession(generation, cancellationToken);
        if (beatmap.IsReplayMode)
        {
            stateMachine.StartReplaySession(sessionCache);
        }
        else
        {
            stateMachine.StartSession(sessionCache, resolvedPlayerId);
        }
        logger.Info("session_started", $"Started {sessionCache.SourceName} {beatmap.Hash}/{beatmap.Difficulty}/{beatmap.Mode}; pages={sessionCache.FetchedPages.Count}.");
        return OverlaySessionStartResult.Started(sessionCache);
    }

    public OverlayUpdateResult Update(BeatmapRunState runState)
    {
        ApplyRuntimeDifficultyRatings(runState);
        var update = stateMachine.Update(runState);
        if (!update.ForcePageRefresh
            && TryResolveBackgroundScanPage(runState, update.Projection, out var scanPage))
        {
            return update.WithPageFetch(
                scanPage,
                fetchPageSize: BeatLeaderScanPageSize,
                mergeFetchedPageAsScan: true);
        }

        return update;
    }

    public void SetRuntimeAlwaysExpand(bool enabled)
    {
        stateMachine.SetRuntimeAlwaysExpand(enabled);
    }

    public async Task<bool> TryFetchSuggestedPageAsync(OverlayUpdateResult updateResult, CancellationToken cancellationToken)
    {
        if (updateResult == null)
        {
            throw new ArgumentNullException(nameof(updateResult));
        }

        if (cache == null || updateResult.PageToFetch == null)
        {
            return false;
        }

        return await TryFetchPageAsync(
            updateResult.PageToFetch.Value,
            cancellationToken,
            updateResult.ForcePageRefresh,
            updateResult.FetchPageSize.GetValueOrDefault(DefaultPageSize),
            updateResult.MergeFetchedPageAsScan).ConfigureAwait(true);
    }

    public void End()
    {
        Interlocked.Increment(ref sessionGeneration);
        if (cache != null)
        {
            retainedCache = cache;
            retainedCacheKey = activeCacheKey;
        }
        cache = null;
        activeCacheKey = string.Empty;
        normalDifficultyRatings = null;
        failedNoFailDifficultyRatings = null;
        failedNoFailRatingsApplied = false;
        lock (pageFetchSync)
        {
            pagesInFlight.Clear();
            scannedRankPages.Clear();
            failedPageRetryAfterUtc.Clear();
        }

        stateMachine.EndSession();
    }

    private void EnsureCurrentSession(long generation, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (generation != Interlocked.Read(ref sessionGeneration))
            throw new OperationCanceledException("The leaderboard session was replaced or ended.", token);
    }

    private static string BuildSessionCacheKey(BeatmapSessionInfo beatmap, string sourceName)
    {
        return $"{NormalizeToken(sourceName)}:{beatmap.Hash.Trim().ToLowerInvariant()}:{NormalizeToken(beatmap.Difficulty)}:{NormalizeToken(beatmap.Mode)}";
    }

    private async Task TryLoadPlayerBestPageAsync(
        BeatmapSessionInfo beatmap,
        string playerId,
        CancellationToken cancellationToken)
    {
        var generation = sessionGeneration;
        var pb = await apiClient.GetPlayerBestAsync(
            "general",
            playerId,
            beatmap.Hash,
            beatmap.Difficulty,
            beatmap.Mode,
            cancellationToken).ConfigureAwait(true);

        EnsureCurrentSession(generation, cancellationToken);
        if (!pb.IsSuccess || pb.Value == null || pb.Value.Rank <= 0 || cache == null)
        {
            return;
        }

        cache.ApplyScore(pb.Value);
        var centerRank = Math.Max(1, pb.Value.Rank);
        var page = cache.GetPageForRank(centerRank);
        if (page > 1 && !cache.FetchedPages.Contains(page))
        {
            await TryFetchPageAsync(page, cancellationToken).ConfigureAwait(true);
        }

    }

    private async Task TryLoadUnrankedBottomPageAsync(LeaderboardSessionCache sessionCache, CancellationToken cancellationToken)
    {
        if (sessionCache.Ranked || sessionCache.TotalScores <= 0)
        {
            return;
        }

        var bottomPage = sessionCache.GetPageForRank(sessionCache.TotalScores);
        if (bottomPage <= 1 || sessionCache.FetchedPages.Contains(bottomPage))
        {
            return;
        }

        await TryFetchPageAsync(bottomPage, cancellationToken).ConfigureAwait(true);
    }

    private async Task<bool> TryFetchPageAsync(
        int page,
        CancellationToken cancellationToken,
        bool forceRefresh = false,
        int count = DefaultPageSize,
        bool mergeAsScan = false)
    {
        var generation = sessionGeneration;
        cancellationToken.ThrowIfCancellationRequested();
        var sessionCache = cache;
        if (sessionCache == null || page < 1 || count < 1)
        {
            return false;
        }

        var fetchKey = BuildFetchKey(page, count, mergeAsScan);
        lock (pageFetchSync)
        {
            var now = DateTimeOffset.UtcNow;
            if (!ReferenceEquals(cache, sessionCache)
                || (!forceRefresh && !mergeAsScan && sessionCache.FetchedPages.Contains(page))
                || (!forceRefresh && mergeAsScan && scannedRankPages.Contains(page))
                || pagesInFlight.Contains(fetchKey)
                || (failedPageRetryAfterUtc.TryGetValue(fetchKey, out var retryAfterUtc) && now < retryAfterUtc))
            {
                return false;
            }

            pagesInFlight.Add(fetchKey);
        }

        try
        {
            var result = await apiClient.GetLeaderboardScoresAsync(
                sessionCache.Hash,
                sessionCache.Difficulty,
                sessionCache.Mode,
                page,
                count,
                cancellationToken).ConfigureAwait(true);

            EnsureCurrentSession(generation, cancellationToken);
            if (!result.IsSuccess || result.Value == null)
            {
                logger.Warn("session_page_fetch_failed", result.ErrorMessage ?? $"Could not fetch page {page}.");
                RememberPageFetchFailure(fetchKey);
                return false;
            }

            if (!ReferenceEquals(cache, sessionCache))
            {
                return false;
            }

            try
            {
                if (mergeAsScan)
                {
                    sessionCache.ApplyScannedPage(result.Value);
                    lock (pageFetchSync)
                    {
                        scannedRankPages.Add(page);
                    }

                    logger.Info("session_scan_page_fetched", $"Scanned leaderboard page {page} at {count} rows.");
                }
                else
                {
                    sessionCache.ApplyPage(result.Value);
                    logger.Info("session_page_fetched", $"Fetched leaderboard page {page}.");
                }

                return true;
            }
            catch (ArgumentException ex)
            {
                logger.Warn("session_page_rejected", ex.Message);
                RememberPageFetchFailure(fetchKey);
                return false;
            }
        }
        finally
        {
            lock (pageFetchSync)
            {
                if (generation == sessionGeneration) pagesInFlight.Remove(fetchKey);
            }
        }
    }

    private void RememberPageFetchFailure(string fetchKey)
    {
        lock (pageFetchSync)
        {
            failedPageRetryAfterUtc[fetchKey] = DateTimeOffset.UtcNow.AddSeconds(IsScoreSaberCache(cache) ? 20 : 8);
        }
    }

    private bool TryResolveBackgroundScanPage(
        BeatmapRunState runState,
        ProjectionResult? projection,
        out int page)
    {
        page = 0;
        var sessionCache = cache;
        if (sessionCache == null
            || projection == null
            || !projection.HasProjection
            || !projection.ProjectedRank.HasValue
            || sessionCache.TotalScores <= sessionCache.ItemsPerPage
            || !string.Equals(sessionCache.SourceName, "BeatLeader", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var progressGatePassed = runState.SongProgressRatio >= 0.04 || runState.ScoredNotes >= 20;
        if (!progressGatePassed || projection.ExactRankCovered || HasCloseKnownAbove(sessionCache, projection))
        {
            return false;
        }

        var totalScanPages = GetScanPageForRank(sessionCache.TotalScores);
        if (totalScanPages <= 0)
        {
            return false;
        }

        var lowerPage = 1;
        var upperPage = totalScanPages;
        var highestPageAboveProjection = 0;
        var sawScannedPage = false;
        foreach (var scannedPage in GetScannedRankPagesSnapshot().OrderBy(value => value))
        {
            if (scannedPage < 1 || scannedPage > totalScanPages || !HasScanPageCoverage(sessionCache, scannedPage))
            {
                continue;
            }

            sawScannedPage = true;
            var relation = ResolveScanPageRelation(sessionCache, projection, scannedPage);
            switch (relation)
            {
                case ScanPageRelation.ContainsProjection:
                    return false;
                case ScanPageRelation.ProjectionBelowPage:
                    lowerPage = Math.Max(lowerPage, scannedPage + 1);
                    highestPageAboveProjection = Math.Max(highestPageAboveProjection, scannedPage);
                    break;
                case ScanPageRelation.ProjectionAbovePage:
                    upperPage = Math.Min(upperPage, scannedPage - 1);
                    break;
            }
        }

        if (!sawScannedPage)
        {
            return TryFindUnscannedScanPage(sessionCache, 1, totalScanPages, 1, out page);
        }

        if (lowerPage > upperPage)
        {
            return false;
        }

        var candidate = upperPage < totalScanPages
            ? lowerPage + ((upperPage - lowerPage) / 2)
            : Math.Min(totalScanPages, Math.Max(lowerPage, Math.Max(1, highestPageAboveProjection) * 2));
        return TryFindUnscannedScanPage(sessionCache, lowerPage, upperPage, candidate, out page);
    }

    private IReadOnlyList<int> GetScannedRankPagesSnapshot()
    {
        lock (pageFetchSync)
        {
            return scannedRankPages.ToList();
        }
    }

    private static string BuildFetchKey(int page, int count, bool mergeAsScan)
    {
        return (mergeAsScan ? "scan:" : "page:") + count.ToString() + ":" + page.ToString();
    }

    private static int GetScanPageForRank(int rank)
    {
        if (rank <= 0)
        {
            return 1;
        }

        return ((rank - 1) / BeatLeaderScanPageSize) + 1;
    }

    private static (int LowerRank, int UpperRank) GetScanRankBounds(LeaderboardSessionCache sessionCache, int scanPage)
    {
        var lowerRank = ((scanPage - 1) * BeatLeaderScanPageSize) + 1;
        var upperRank = scanPage * BeatLeaderScanPageSize;
        if (sessionCache.TotalScores > 0)
        {
            upperRank = Math.Min(upperRank, sessionCache.TotalScores);
        }

        return (lowerRank, upperRank);
    }

    private static bool HasScanPageCoverage(LeaderboardSessionCache sessionCache, int scanPage)
    {
        var (lowerRank, upperRank) = GetScanRankBounds(sessionCache, scanPage);
        for (var rank = lowerRank; rank <= upperRank; rank++)
        {
            if (!sessionCache.ScoresByRank.ContainsKey(rank))
            {
                return false;
            }
        }

        return true;
    }

    private bool TryFindUnscannedScanPage(
        LeaderboardSessionCache sessionCache,
        int lowerPage,
        int upperPage,
        int candidatePage,
        out int page)
    {
        page = 0;
        lowerPage = Math.Max(1, lowerPage);
        upperPage = Math.Max(lowerPage, upperPage);
        candidatePage = Math.Max(lowerPage, Math.Min(upperPage, candidatePage));

        for (var offset = 0; offset <= upperPage - lowerPage; offset++)
        {
            var lowerCandidate = candidatePage - offset;
            if (lowerCandidate >= lowerPage && IsScanPageFetchable(sessionCache, lowerCandidate))
            {
                page = lowerCandidate;
                return true;
            }

            var upperCandidate = candidatePage + offset;
            if (upperCandidate != lowerCandidate
                && upperCandidate <= upperPage
                && IsScanPageFetchable(sessionCache, upperCandidate))
            {
                page = upperCandidate;
                return true;
            }
        }

        return false;
    }

    private bool IsScanPageFetchable(LeaderboardSessionCache sessionCache, int scanPage)
    {
        lock (pageFetchSync)
        {
            if (scannedRankPages.Contains(scanPage)
                || pagesInFlight.Contains(BuildFetchKey(scanPage, BeatLeaderScanPageSize, mergeAsScan: true)))
            {
                return false;
            }
        }

        return !HasScanPageCoverage(sessionCache, scanPage);
    }

    private static ScanPageRelation ResolveScanPageRelation(
        LeaderboardSessionCache sessionCache,
        ProjectionResult projection,
        int scanPage)
    {
        var (lowerRank, upperRank) = GetScanRankBounds(sessionCache, scanPage);
        var rows = sessionCache.ScoresByRank.Values
            .Where(row => row.Rank >= lowerRank && row.Rank <= upperRank)
            .OrderBy(row => row.Rank)
            .ToList();
        if (rows.Count == 0)
        {
            return ScanPageRelation.Unknown;
        }

        var first = rows[0];
        var last = rows[rows.Count - 1];
        if (IsProjectionBetterOrEqual(sessionCache, projection, first))
        {
            return ScanPageRelation.ProjectionAbovePage;
        }

        if (!IsProjectionBetterOrEqual(sessionCache, projection, last))
        {
            return ScanPageRelation.ProjectionBelowPage;
        }

        return ScanPageRelation.ContainsProjection;
    }

    private static bool HasCloseKnownAbove(LeaderboardSessionCache sessionCache, ProjectionResult projection)
    {
        double? bestPpGap = null;
        double? bestScoreGapRatio = null;
        var scoreGapDenominator = ResolveScoreGapDenominator(sessionCache);
        foreach (var row in sessionCache.ScoresByRank.Values)
        {
            if (IsProjectionBetterOrEqual(sessionCache, projection, row))
            {
                continue;
            }

            if (TryResolvePpGap(sessionCache, projection, row, out var ppGap))
            {
                bestPpGap = !bestPpGap.HasValue ? ppGap : Math.Min(bestPpGap.Value, ppGap);
            }
            else if (TryResolveScoreGapRatio(projection, row, scoreGapDenominator, out var scoreGapRatio))
            {
                bestScoreGapRatio = !bestScoreGapRatio.HasValue ? scoreGapRatio : Math.Min(bestScoreGapRatio.Value, scoreGapRatio);
            }
        }

        if (bestPpGap.HasValue)
        {
            var projectedPp = projection.RankingPp ?? projection.ProjectedPp;
            var closeEnoughGap = Math.Min(
                RankedScanMaxPpGap,
                Math.Max(RankedScanMinPpGap, projectedPp.GetValueOrDefault() * RankedScanPpGapRatio));
            return bestPpGap.Value <= closeEnoughGap;
        }

        return bestScoreGapRatio.HasValue && bestScoreGapRatio.Value <= ScoreScanCloseEnoughRatio;
    }

    private static bool TryResolvePpGap(
        LeaderboardSessionCache sessionCache,
        ProjectionResult projection,
        BeatLeaderScoreRow row,
        out double gap)
    {
        gap = 0;
        if (!sessionCache.RankByPp || !projection.RankingPp.HasValue || !row.Pp.HasValue || row.Pp.Value <= 0)
        {
            return false;
        }

        gap = row.Pp.Value - projection.RankingPp.Value;
        return gap >= 0;
    }

    private static bool TryResolveScoreGapRatio(
        ProjectionResult projection,
        BeatLeaderScoreRow row,
        int denominator,
        out double gapRatio)
    {
        gapRatio = 0;
        var projectedScore = projection.ProjectedFinalScore.GetValueOrDefault();
        if (projectedScore <= 0 || row.ModifiedScore <= projectedScore || denominator <= 0)
        {
            return false;
        }

        gapRatio = (double)(row.ModifiedScore - projectedScore) / denominator;
        return gapRatio >= 0;
    }

    private static int ResolveScoreGapDenominator(LeaderboardSessionCache sessionCache)
    {
        var configuredMax = sessionCache.EffectiveMaxScore ?? sessionCache.MaxScore;
        if (configuredMax.HasValue && configuredMax.Value > 0)
        {
            return configuredMax.Value;
        }

        return sessionCache.ScoresByRank.Values
            .Select(score => Math.Max(1, score.ModifiedScore))
            .DefaultIfEmpty(1)
            .Max();
    }

    private static bool IsProjectionBetterOrEqual(
        LeaderboardSessionCache sessionCache,
        ProjectionResult projection,
        BeatLeaderScoreRow row)
    {
        if (sessionCache.RankByPp && projection.RankingPp.HasValue && row.Pp.GetValueOrDefault() > 0)
        {
            return projection.RankingPp.Value >= row.Pp!.Value;
        }

        return projection.ProjectedFinalScore.GetValueOrDefault() >= row.ModifiedScore;
    }

    private enum ScanPageRelation
    {
        Unknown,
        ProjectionAbovePage,
        ProjectionBelowPage,
        ContainsProjection
    }

    private async Task TryLoadDifficultyRatingsAsync(
        BeatmapSessionInfo beatmap,
        LeaderboardSessionCache sessionCache,
        CancellationToken cancellationToken)
    {
        var generation = sessionGeneration;
        try
        {
            var song = await apiClient.GetSongByHashAsync(beatmap.Hash, cancellationToken).ConfigureAwait(true);
            EnsureCurrentSession(generation, cancellationToken);
            if (!song.IsSuccess || song.Value == null)
            {
                logger.Warn("session_difficulty_fetch_failed", song.ErrorMessage ?? "BeatLeader map response was empty.");
                return;
            }

            var difficulties = song.Value.ResolveDifficulties();
            if (difficulties.Count == 0)
            {
                logger.Warn("session_difficulty_fetch_failed", "BeatLeader map response did not include difficulties.");
                return;
            }

            var matched = difficulties
                .FirstOrDefault(difficulty =>
                    ModeMatches(difficulty.ModeName, beatmap.Mode) &&
                    DifficultyMatches(difficulty.DifficultyName, beatmap.Difficulty));

            if (matched == null)
            {
                matched = difficulties
                    .FirstOrDefault(difficulty => ModeMatches(difficulty.ModeName, beatmap.Mode));
            }

            if (matched == null)
            {
                logger.Warn("session_difficulty_match_failed", $"No BeatLeader difficulty matched {beatmap.Difficulty}/{beatmap.Mode}; candidates={string.Join(",", difficulties.Select(d => $"{d.DifficultyName}/{d.ModeName}"))}.");
                return;
            }

            sessionCache.ApplyMaxScore(matched.MaxScore);
            normalDifficultyRatings = ResolveDifficultyRatings(matched, beatmap.ActiveModifiers, includeNoFailPenalty: false);
            normalDifficultyRatings.Value.ApplyTo(sessionCache);
            logger.Info(
                "session_difficulty_ratings_applied",
                $"source=map_hash_difficulty; difficulty={matched.DifficultyName}/{matched.ModeName}; ranked={sessionCache.Ranked}; maxScore={sessionCache.MaxScore?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "(none)"}; stars={sessionCache.StarRating?.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) ?? "(none)"}; pass={sessionCache.PassRating?.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) ?? "(none)"}; acc={sessionCache.AccRating?.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) ?? "(none)"}; tech={sessionCache.TechRating?.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) ?? "(none)"}; modifierMultiplier={sessionCache.ModifierMultiplier:0.###}.");
            LogScoreModifierState(
                "session_difficulty_modifier_values_applied",
                "source=map_hash_difficulty",
                beatmap.ActiveModifiers,
                sessionCache);
            failedNoFailRatingsApplied = false;

            if (BeatLeaderModifierPolicy.HasNoFail(beatmap.ActiveModifiers))
            {
                failedNoFailDifficultyRatings = ResolveDifficultyRatings(matched, beatmap.ActiveModifiers, includeNoFailPenalty: true);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            EnsureCurrentSession(generation, cancellationToken);
            logger.Warn("session_difficulty_fetch_failed", ex.Message);
        }
    }

    private static DifficultyRatingSnapshot ResolveDifficultyRatings(BeatLeaderMapDifficultyDto difficulty, IReadOnlyList<string> activeModifiers, bool includeNoFailPenalty)
    {
        var stars = difficulty.Stars;
        var predictedAcc = difficulty.PredictedAcc;
        var passRating = difficulty.PassRating;
        var accRating = difficulty.AccRating;
        var techRating = difficulty.TechRating;
        var modifierMultiplier = 1d;
        var scoringModifiers = BeatLeaderModifierPolicy.GetScoringModifiers(activeModifiers, includeNoFailPenalty);
        var scoreModifierMultiplier = 1d;
        var positiveScoreModifierMultiplier = 1d;
        var negativeScoreModifierMultiplier = 1d;
        if (difficulty.TryResolveScoreModifierMultipliers(scoringModifiers, out var positiveMultiplier, out var negativeMultiplier))
        {
            positiveScoreModifierMultiplier = positiveMultiplier;
            negativeScoreModifierMultiplier = negativeMultiplier;
            scoreModifierMultiplier = positiveMultiplier * negativeMultiplier;
        }

        if (difficulty.TryResolveModifierRatings(scoringModifiers, out var modifierRatings))
        {
            stars = modifierRatings.Stars ?? stars;
            predictedAcc = modifierRatings.PredictedAcc ?? predictedAcc;
            passRating = modifierRatings.PassRating ?? passRating;
            accRating = modifierRatings.AccRating ?? accRating;
            techRating = modifierRatings.TechRating ?? techRating;
            modifierMultiplier = modifierRatings.Multiplier;
        }

        return new DifficultyRatingSnapshot(stars, predictedAcc, passRating, accRating, techRating, modifierMultiplier, scoreModifierMultiplier, positiveScoreModifierMultiplier, negativeScoreModifierMultiplier);
    }

    private void TryApplyLeaderboardContainerScoreModifiers(
        LeaderboardContainer? container,
        IReadOnlyList<string> activeModifiers,
        LeaderboardSessionCache sessionCache,
        bool includeNoFailPenalty)
    {
        if (container == null || activeModifiers == null || activeModifiers.Count == 0)
        {
            return;
        }

        var scoringModifiers = BeatLeaderModifierPolicy.GetScoringModifiers(activeModifiers, includeNoFailPenalty);
        if (scoringModifiers.Count == 0)
        {
            return;
        }

        if (!container.TryResolveScoreModifierMultipliers(scoringModifiers, out var positiveMultiplier, out var negativeMultiplier))
        {
            return;
        }

        sessionCache.ApplyScoreModifierMultipliers(positiveMultiplier, negativeMultiplier);
        logger.Info(
            "session_score_modifier_values_applied",
            $"source=leaderboard_container; mods={string.Join(",", scoringModifiers)}; positive={positiveMultiplier:0.####}; negative={negativeMultiplier:0.####}; total={positiveMultiplier * negativeMultiplier:0.####}.");
    }

    private void LogScoreModifierState(
        string eventName,
        string source,
        IReadOnlyList<string> activeModifiers,
        LeaderboardSessionCache sessionCache)
    {
        var scoringModifiers = BeatLeaderModifierPolicy.GetScoringModifiers(activeModifiers, includeNoFailPenalty: false);
        if (scoringModifiers.Count == 0)
        {
            return;
        }

        logger.Info(
            eventName,
            $"{source}; mods={string.Join(",", scoringModifiers)}; ranked={sessionCache.Ranked}; maxScore={sessionCache.MaxScore?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "(none)"}; positive={sessionCache.PositiveScoreModifierMultiplier:0.####}; negative={sessionCache.NegativeScoreModifierMultiplier:0.####}; total={sessionCache.ScoreModifierMultiplier:0.####}.");
    }

    private void ApplyRuntimeDifficultyRatings(BeatmapRunState runState)
    {
        if (cache == null || runState == null)
        {
            return;
        }

        if (runState.IsFailedWithNoFail)
        {
            if (!failedNoFailRatingsApplied && failedNoFailDifficultyRatings.HasValue)
            {
                failedNoFailDifficultyRatings.Value.ApplyTo(cache);
                failedNoFailRatingsApplied = true;
                logger.Info("session_modifier_ratings_applied", $"Applied {cache.SourceName} No Fail penalty after real fail.");
            }
            else if (IsScoreSaberCache(cache))
            {
                TryApplyScoreSaberScoreModifiers(cache, runState.ActiveModifiers, includeNoFailPenalty: true);
                failedNoFailRatingsApplied = true;
            }

            return;
        }

        if (failedNoFailRatingsApplied && normalDifficultyRatings.HasValue)
        {
            normalDifficultyRatings.Value.ApplyTo(cache);
            failedNoFailRatingsApplied = false;
        }
        else if (failedNoFailRatingsApplied && IsScoreSaberCache(cache))
        {
            TryApplyScoreSaberScoreModifiers(cache, runState.ActiveModifiers, includeNoFailPenalty: false);
            failedNoFailRatingsApplied = false;
        }
    }

    private void TryApplyScoreSaberScoreModifiers(
        LeaderboardSessionCache sessionCache,
        IReadOnlyList<string> activeModifiers,
        bool includeNoFailPenalty)
    {
        if (!IsScoreSaberCache(sessionCache))
        {
            return;
        }

        if (!ScoreSaberModifierPolicy.TryResolveScoreModifierMultipliers(
                activeModifiers,
                includeNoFailPenalty,
                sessionCache.PositiveModifiers,
                out var positiveMultiplier,
                out var negativeMultiplier))
        {
            sessionCache.ApplyScoreModifierMultipliers(1d, 1d);
            return;
        }

        sessionCache.ApplyScoreModifierMultipliers(positiveMultiplier, negativeMultiplier);
        logger.Info(
            "session_scoresaber_modifier_values_applied",
            $"mods={string.Join(",", activeModifiers ?? Array.Empty<string>())}; positiveEnabled={sessionCache.PositiveModifiers}; positive={positiveMultiplier:0.####}; negative={negativeMultiplier:0.####}; total={positiveMultiplier * negativeMultiplier:0.####}.");
    }

    private static bool IsScoreSaberCache(LeaderboardSessionCache? sessionCache)
    {
        return string.Equals(sessionCache?.SourceName, "ScoreSaber", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ModeMatches(string? left, string? right)
    {
        return string.Equals(NormalizeToken(left), NormalizeToken(right), StringComparison.OrdinalIgnoreCase);
    }

    private static bool DifficultyMatches(string? left, string? right)
    {
        return string.Equals(NormalizeToken(left), NormalizeToken(right), StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeToken(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return value.Replace(" ", string.Empty).Replace("+", "Plus").Trim();
    }

    private readonly struct DifficultyRatingSnapshot
    {
        public DifficultyRatingSnapshot(
            double? stars,
            double? predictedAcc,
            double? passRating,
            double? accRating,
            double? techRating,
            double modifierMultiplier,
            double scoreModifierMultiplier,
            double positiveScoreModifierMultiplier,
            double negativeScoreModifierMultiplier)
        {
            Stars = stars;
            PredictedAcc = predictedAcc;
            PassRating = passRating;
            AccRating = accRating;
            TechRating = techRating;
            ModifierMultiplier = modifierMultiplier;
            ScoreModifierMultiplier = scoreModifierMultiplier;
            PositiveScoreModifierMultiplier = positiveScoreModifierMultiplier;
            NegativeScoreModifierMultiplier = negativeScoreModifierMultiplier;
        }

        private double? Stars { get; }

        private double? PredictedAcc { get; }

        private double? PassRating { get; }

        private double? AccRating { get; }

        private double? TechRating { get; }

        private double ModifierMultiplier { get; }

        private double ScoreModifierMultiplier { get; }

        private double PositiveScoreModifierMultiplier { get; }

        private double NegativeScoreModifierMultiplier { get; }

        public void ApplyTo(LeaderboardSessionCache sessionCache)
        {
            sessionCache.ApplyDifficultyRatings(
                Stars,
                PredictedAcc,
                PassRating,
                AccRating,
                TechRating,
                ModifierMultiplier,
                ScoreModifierMultiplier,
                PositiveScoreModifierMultiplier,
                NegativeScoreModifierMultiplier);
        }
    }
}
