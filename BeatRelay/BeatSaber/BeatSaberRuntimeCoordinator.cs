#if NETFRAMEWORK
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using BeatRelay.BeatLeader;
using BeatRelay.Config;
using BeatRelay.Diagnostics;
using BeatRelay.ModMenu;
using BeatRelay.Ranking;
using BeatRelay.ScoreSaber;
using BeatRelay.Sessions;
using BeatRelay.UI;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace BeatRelay.BeatSaber;

public sealed class BeatSaberRuntimeCoordinator : IDisposable
{
    private const double CollapseBeforeNextNoteSeconds = 1.25d;
    private const double BreakTimelineCheckIntervalSeconds = 0.1d;
    private const int PreviewPageSize = 10;
    private const int MaxCustomizationPreviewPagesPerMap = 30;
    private const int MaxCustomizationPreviewPagesPerRefresh = 2;
    private static readonly (double Accuracy, double Value)[] BeatLeaderSampleAccCurve =
    {
        (1.0d, 7.424d),
        (0.999d, 6.241d),
        (0.9975d, 5.158d),
        (0.995d, 4.010d),
        (0.9925d, 3.241d),
        (0.99d, 2.700d),
        (0.9875d, 2.303d),
        (0.985d, 2.007d),
        (0.9825d, 1.786d),
        (0.98d, 1.618d),
        (0.9775d, 1.490d),
        (0.975d, 1.392d),
        (0.9725d, 1.315d),
        (0.97d, 1.256d),
        (0.965d, 1.167d),
        (0.96d, 1.094d),
        (0.955d, 1.039d),
        (0.95d, 1.000d),
        (0.94d, 0.931d),
        (0.93d, 0.867d),
        (0.92d, 0.813d),
        (0.91d, 0.768d),
        (0.9d, 0.729d),
        (0.875d, 0.650d),
        (0.85d, 0.581d),
        (0.825d, 0.522d),
        (0.8d, 0.473d),
        (0.75d, 0.404d),
        (0.7d, 0.345d),
        (0.65d, 0.296d),
        (0.6d, 0.256d),
        (0.0d, 0.0d)
    };
    private static readonly string ReplayVerificationMarker = "replay_config_always_expand_restores_after_exit";
    private static readonly Regex BeatLeaderReplayPlayerIdPattern = new(@"^(?<id>\d{16,20})-", RegexOptions.Compiled);
    private static readonly Func<object, object?> MissingMemberReader = _ => null;
    private static readonly ConcurrentDictionary<MemberLookupKey, Func<object, object?>> MemberReaders = new();

    private readonly OverlayConfig config;
    private readonly IOverlayLogger logger;
    private readonly IBeatLeaderApiClient apiClient;
    private readonly BeatLeaderOverlaySessionManager sessionManager;
    private InGameOverlayRenderer? overlayRenderer;
    private readonly ReplayAlwaysExpandOverride replayAlwaysExpandOverride;

    private CancellationTokenSource? sessionCancellation;
    private DateTimeOffset lastOverlaySnapshotUtc = DateTimeOffset.MinValue;
    private DateTimeOffset lastRuntimePageFetchUtc = DateTimeOffset.MinValue;
    private bool sessionActive;
    private bool pageFetchInFlight;
    private bool isPaused;
    private IReadOnlyList<string> activeModifiers = Array.Empty<string>();
    private IReadOnlyList<double> activeNoteTimes = Array.Empty<double>();
    private IReadOnlyList<BreakWindow> activeBreakWindows = Array.Empty<BreakWindow>();
    private double? activeSongLengthSeconds;
    private double? activeBeatsPerMinute;
    private bool runtimeBeatmapTimelineLoadAttempted;
    private int currentActiveBreakWindowIndex = -1;
    private double lastKnownSongTime;
    private DateTimeOffset lastBreakTimelineCheckUtc = DateTimeOffset.MinValue;
    private bool lastTimelineBreakExpanded;
    private bool hudSuppressedByGame;
    private bool activeReplayMode;
    private bool pendingReplayMode;
    private readonly object customizationPreviewSync = new();
    private readonly Dictionary<string, LeaderboardSessionCache> customizationPreviewCaches = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> customizationPreviewRatingsChecked = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? customizationPreviewCancellation;
    private OverlayViewModel? pendingCustomizationPreviewViewModel;
    private int customizationPreviewGeneration;
    private bool customizationPreviewActive;
    private bool customizationPreviewWorldSuppressed;
    private bool levelFailedWithNoFail;
    private bool sessionStartupInFlight;
    private BeatmapSessionInfo? lastDetectedBeatmap;
    private OverlayViewModel lastRendererViewModel = OverlayViewModel.Hidden();
    private int runtimeReplayContextChecksRemaining;
    private object? cachedRuntimeScoreController;
    private object? cachedAudioTimeSyncController;
    private object? cachedGameplayCoreSceneSetupData;
    private object? cachedLevelScenesTransitionSetupData;
    private object? cachedStandardGameplaySceneSetupData;
    private object? cachedGameplayModifiers;
    private object? cachedPlayerSpecificSettings;

    public event Action? CustomizationPreviewDataApplied;

    public BeatSaberRuntimeCoordinator(OverlayConfig config, IOverlayLogger logger)
    {
        GC.KeepAlive(ReplayVerificationMarker);
        this.config = config ?? throw new ArgumentNullException(nameof(config));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        replayAlwaysExpandOverride = new ReplayAlwaysExpandOverride(config);

        var httpClient = new HttpClient();
        var beatLeaderClient = new BeatLeaderApiClient(httpClient, new BeatLeaderRateLimiter(), logger);
        var scoreSaberClient = new ScoreSaberApiClient(httpClient, new BeatLeaderRateLimiter(), logger);
        apiClient = new LeaderboardApiClientSelector(config, beatLeaderClient, scoreSaberClient);
        sessionManager = new BeatLeaderOverlaySessionManager(
            apiClient,
            new OverlayStateMachine(config),
            logger);
        overlayRenderer = InGameOverlayRenderer.Create(config, logger);
        SceneManager.activeSceneChanged += OnActiveSceneChanged;
        SceneManager.sceneLoaded += OnSceneLoaded;
        EnsureOverlayInitialized();
    }

    public void StartDetectedSong(BeatmapSessionInfo beatmap)
    {
        if (beatmap == null)
        {
            throw new ArgumentNullException(nameof(beatmap));
        }

        EnsureOverlayInitialized();
        lastDetectedBeatmap = CloneBeatmapSessionInfo(beatmap);
        sessionCancellation?.Cancel();
        sessionCancellation?.Dispose();
        sessionCancellation = new CancellationTokenSource();
        ClearCustomizationPreviewCache();
        var wasReplayPending = pendingReplayMode;
        ResetRunState();
        activeReplayMode = wasReplayPending || IsReplaySession(beatmap);
        beatmap.IsReplayMode = activeReplayMode;
        activeModifiers = ParseActiveModifiers(beatmap.ActiveModifiers);
        SetReplayAlwaysExpandOverride(activeReplayMode);
        sessionManager.SetRuntimeAlwaysExpand(activeReplayMode);
        overlayRenderer?.Clear();
        overlayRenderer?.SetReplayModeRenderingEnabled(activeReplayMode);

        var suppressionReason = ResolveSuppressionReason(beatmap);
        if (suppressionReason != OverlaySuppressionReason.None)
        {
            SuppressOverlaySession(suppressionReason);
            return;
        }

        overlayRenderer?.SetGameplayActive(true);
        overlayRenderer?.SetHudSuppressed(false);
        overlayRenderer?.SetWorldVisible(true);
        SetRendererViewModel(new OverlayViewModel
        {
            Mode = activeReplayMode ? OverlayMode.Expanded : OverlayMode.Calculating,
            MessageText = "--"
        });
        activeSongLengthSeconds = beatmap.SongLengthSeconds;
        activeBeatsPerMinute = beatmap.BeatsPerMinute;

        if (!activeReplayMode)
        {
            activeNoteTimes = NormalizeNoteTimes(beatmap.NoteTimes ?? Array.Empty<double>(), activeSongLengthSeconds, activeBeatsPerMinute);

            activeBreakWindows = BuildBreakWindows(activeNoteTimes);
        }
        else
        {
            activeNoteTimes = Array.Empty<double>();
            activeBreakWindows = Array.Empty<BreakWindow>();
        }

        currentActiveBreakWindowIndex = -1;
        lastKnownSongTime = 0d;
        lastBreakTimelineCheckUtc = DateTimeOffset.MinValue;
        lastTimelineBreakExpanded = false;
        runtimeBeatmapTimelineLoadAttempted = false;
        logger.Info(
            "runtime_note_timing_loaded",
            $"source=session; activeNoteTimes={activeNoteTimes.Count}; lastNoteTime={(activeNoteTimes.Count > 0 ? activeNoteTimes[activeNoteTimes.Count - 1].ToString("0.000") : "none")}; breakWindows={activeBreakWindows.Count}; bpm={(activeBeatsPerMinute.HasValue ? activeBeatsPerMinute.Value.ToString("0.###") : "unknown")}; songLength={(activeSongLengthSeconds.HasValue ? activeSongLengthSeconds.Value.ToString("0.###") : "unknown")}");
        RefreshHudSuppression(scoreController: null);

        sessionStartupInFlight = true;
        _ = StartDetectedSongAsync(beatmap, sessionCancellation.Token);
    }

    public void Dispose()
    {
        sessionManager.End();
        SceneManager.activeSceneChanged -= OnActiveSceneChanged;
        SceneManager.sceneLoaded -= OnSceneLoaded;
        sessionCancellation?.Cancel();
        sessionCancellation?.Dispose();
        sessionCancellation = null;
        customizationPreviewCancellation?.Cancel();
        customizationPreviewCancellation?.Dispose();
        customizationPreviewCancellation = null;
        RestoreReplayAlwaysExpandOverride();
        overlayRenderer?.Clear();
        if (overlayRenderer != null)
        {
            UnityEngine.Object.Destroy(overlayRenderer.gameObject);
        }
    }

    public void RecordScoreControllerUpdate(object scoreController)
    {
        if (!sessionActive && TryResumeRestartedSession(scoreController))
        {
            return;
        }

        if (!sessionActive || scoreController == null)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var effectiveIntervalSeconds = Math.Max(0.25, Math.Min(3.0, config.UpdateIntervalSeconds));
        var overlaySnapshotDue = now - lastOverlaySnapshotUtc >= TimeSpan.FromSeconds(effectiveIntervalSeconds);
        var breakTimelineCheckDue = now - lastBreakTimelineCheckUtc >= TimeSpan.FromSeconds(BreakTimelineCheckIntervalSeconds);
        if (!overlaySnapshotDue && !breakTimelineCheckDue)
        {
            return;
        }

        var sampleStarted = config.DebugLogging
            ? System.Diagnostics.Stopwatch.GetTimestamp()
            : 0L;
        EnsureOverlayInitialized();
        EnsureRuntimeReferences(scoreController);
        if (!activeReplayMode && runtimeReplayContextChecksRemaining > 0)
        {
            runtimeReplayContextChecksRemaining--;
            if (IsReplayRuntimeContext(scoreController))
            {
                MarkReplayModeDetected();
            }
        }

        RefreshHudSuppression(scoreController);
        var runState = BuildRunState(scoreController, now);
        var timelineBreakExpanded = IsTimelineBreakExpanded(runState);
        var breakStateChanged = breakTimelineCheckDue && timelineBreakExpanded != lastTimelineBreakExpanded;
        if (breakTimelineCheckDue)
        {
            lastBreakTimelineCheckUtc = now;
            lastTimelineBreakExpanded = timelineBreakExpanded;
        }

        if (!breakStateChanged && !overlaySnapshotDue)
        {
            return;
        }

        lastOverlaySnapshotUtc = now;
        var updateStarted = config.DebugLogging
            ? System.Diagnostics.Stopwatch.GetTimestamp()
            : 0L;
        var update = sessionManager.Update(runState);
        if (activeReplayMode)
        {
            update.ViewModel.Mode = OverlayMode.Expanded;
        }
        SetRendererViewModel(update.ViewModel);
        overlayRenderer?.SetHudSuppressed(hudSuppressedByGame);
        overlayRenderer?.SetWorldVisible(IsLikelyGameplayScene(SceneManager.GetActiveScene().name) && config.Enabled && !hudSuppressedByGame);
        if (config.DebugLogging)
        {
            var updateMilliseconds = ElapsedMilliseconds(updateStarted);
            var totalMilliseconds = ElapsedMilliseconds(sampleStarted);
            logger.Info(
                "runtime_overlay_snapshot",
                $"{FormatViewModel(update.ViewModel)}; modeResolved={update.ViewModel.Mode}; score={runState.CurrentModifiedScore}; progress={runState.SongProgressRatio:0.000}; notes={runState.ScoredNotes}; misses={runState.MissCount}; badCuts={runState.BadCutCount}; songTime={runState.SongTimeSeconds:0.000}; lastNoteTime={GetLastPlannedNoteTime():0.000}; secondsSinceLastNote={runState.SecondsSinceLastNote:0.000}; secondsUntilNextScorableNote={(double.IsPositiveInfinity(runState.SecondsUntilNextScorableNote) ? "Infinity" : runState.SecondsUntilNextScorableNote.ToString("0.000"))}; isInPlannedBreak={runState.IsInPlannedBreak}; isAfterLastNote={runState.IsAfterLastNote}; hasUpcomingScorableNote={runState.HasUpcomingScorableNote}; updateMs={updateMilliseconds:0.###}; totalMs={totalMilliseconds:0.###}");
        }

        if (update.PageToFetch.HasValue)
        {
            _ = FetchSuggestedPageAsync(update);
        }
    }

    public void MarkReplayModeDetected()
    {
        pendingReplayMode = true;
        activeReplayMode = true;
        SetReplayAlwaysExpandOverride(true);
        sessionManager.SetRuntimeAlwaysExpand(true);
        activeNoteTimes = Array.Empty<double>();
        activeBreakWindows = Array.Empty<BreakWindow>();
        runtimeBeatmapTimelineLoadAttempted = true;
        currentActiveBreakWindowIndex = -1;
        lastTimelineBreakExpanded = false;
        overlayRenderer?.SetReplayModeRenderingEnabled(true);
        logger.Info("runtime_replay_mode_detected", $"Replay lifecycle hook marked the current session as replay. marker={ReplayVerificationMarker}");
    }

    public void MarkLevelFailed()
    {
        if (!sessionActive || activeReplayMode)
        {
            return;
        }

        if (!BeatLeaderModifierPolicy.HasNoFail(activeModifiers))
        {
            return;
        }

        if (levelFailedWithNoFail)
        {
            return;
        }

        levelFailedWithNoFail = true;
        lastOverlaySnapshotUtc = DateTimeOffset.MinValue;
        lastRuntimePageFetchUtc = DateTimeOffset.MinValue;
        logger.Info("runtime_no_fail_penalty_active", "Real level fail detected while No Fail is enabled.");
    }

    private void SetReplayAlwaysExpandOverride(bool enabled)
    {
        if (!enabled)
        {
            RestoreReplayAlwaysExpandOverride();
            return;
        }

        var wasActive = replayAlwaysExpandOverride.IsActive;
        replayAlwaysExpandOverride.Enable();
        logger.Info("runtime_replay_always_expand_override", $"AlwaysExpand temporarily enabled for replay; previous={replayAlwaysExpandOverride.PreviousAlwaysExpand}; alreadyActive={wasActive}; marker={ReplayVerificationMarker}");
    }

    private void RestoreReplayAlwaysExpandOverride()
    {
        if (!replayAlwaysExpandOverride.IsActive)
        {
            return;
        }

        replayAlwaysExpandOverride.Restore();
        logger.Info("runtime_replay_always_expand_restored", $"AlwaysExpand restored after replay; restored={config.AlwaysExpand}; marker={ReplayVerificationMarker}");
    }

    public void SetPaused(bool paused)
    {
        EnsureOverlayInitialized();
        isPaused = paused;
        overlayRenderer?.SetPaused(paused);
        overlayRenderer?.SetWorldVisible(IsLikelyGameplayScene(SceneManager.GetActiveScene().name) && config.Enabled && !hudSuppressedByGame && (sessionActive || paused));
    }

    public void RefreshOverlayConfiguration()
    {
        EnsureOverlayInitialized();
        overlayRenderer?.Configure(config);
    }

    public void SetCustomizationPreviewVisible(bool visible, OverlayViewModel? previewViewModel = null)
    {
        EnsureOverlayInitialized();
        if (overlayRenderer == null)
        {
            return;
        }

        if (!visible)
        {
            customizationPreviewActive = false;
            ClearCustomizationPreviewCache();
            customizationPreviewWorldSuppressed = false;
            overlayRenderer.SetCustomizationPreviewActive(false);
            if (!sessionActive && !isPaused)
            {
                SetRendererViewModel(OverlayViewModel.Hidden());
            }

            overlayRenderer.SetWorldVisible(IsLikelyGameplayScene(SceneManager.GetActiveScene().name) && config.Enabled && !hudSuppressedByGame && sessionActive && !isPaused);
            overlayRenderer.SetGameplayActive(IsLikelyGameplayScene(SceneManager.GetActiveScene().name) && sessionActive);
            return;
        }

        if (!IsLikelyMenuScene(SceneManager.GetActiveScene().name))
        {
            SetCustomizationPreviewVisible(false);
            return;
        }

        customizationPreviewActive = true;
        overlayRenderer.Configure(config);
        overlayRenderer.SetCustomizationPreviewActive(true);
        overlayRenderer.SetGameplayActive(true);
        overlayRenderer.SetWorldVisible(!customizationPreviewWorldSuppressed);
        overlayRenderer.SetHudSuppressed(false);
        SetRendererViewModel(previewViewModel ?? BuildCustomizationPreviewViewModel(null, 95f, config.PreviewExpanded));
    }

    public void UpdateCustomizationPreview(OverlayViewModel previewViewModel)
    {
        EnsureOverlayInitialized();
        if (overlayRenderer == null)
        {
            return;
        }

        if (!customizationPreviewActive || !IsLikelyMenuScene(SceneManager.GetActiveScene().name))
        {
            return;
        }

        overlayRenderer.Configure(config);
        overlayRenderer.SetCustomizationPreviewActive(true);
        overlayRenderer.SetGameplayActive(true);
        overlayRenderer.SetWorldVisible(!customizationPreviewWorldSuppressed);
        overlayRenderer.SetHudSuppressed(false);
        SetRendererViewModel(previewViewModel);
    }

    public void SetCustomizationPreviewWorldVisible(bool visible)
    {
        EnsureOverlayInitialized();
        if (overlayRenderer == null)
        {
            return;
        }

        if (!customizationPreviewActive || !IsLikelyMenuScene(SceneManager.GetActiveScene().name))
        {
            overlayRenderer.SetWorldVisible(false);
            return;
        }

        customizationPreviewWorldSuppressed = !visible;
        overlayRenderer.SetWorldVisible(visible);
        if (!visible)
        {
            return;
        }

        overlayRenderer.SetCustomizationPreviewActive(true);
        overlayRenderer.SetGameplayActive(true);
        overlayRenderer.SetHudSuppressed(false);
    }

    public OverlayViewModel BuildCustomizationPreviewViewModel(SampleBeatmapInfo? sampleMap, float accuracy, bool expanded)
    {
        sampleMap ??= SampleBeatmapInfo.Fallback;
        LeaderboardSessionCache? cached = null;
        if (sampleMap.HasBeatLeaderLookup)
        {
            lock (customizationPreviewSync)
            {
                customizationPreviewCaches.TryGetValue(BuildCustomizationPreviewCacheKey(sampleMap, config.LeaderboardSource), out cached);
            }
        }

        if (cached != null)
        {
            lock (customizationPreviewSync)
            {
                return BuildCustomizationPreviewViewModel(sampleMap, accuracy, expanded, cached);
            }
        }

        return BuildCustomizationPreviewViewModel(sampleMap, accuracy, expanded, cached);
    }

    public bool HasCustomizationPreviewCache(SampleBeatmapInfo? sampleMap)
    {
        if (sampleMap == null || !sampleMap.HasBeatLeaderLookup)
        {
            return false;
        }

        lock (customizationPreviewSync)
        {
            return customizationPreviewCaches.TryGetValue(BuildCustomizationPreviewCacheKey(sampleMap, config.LeaderboardSource), out var cache)
                && cache.HasUsableRows;
        }
    }

    public bool TryGetCustomizationPreviewRankInfo(SampleBeatmapInfo? sampleMap, out bool ranked, out double? stars)
    {
        ranked = false;
        stars = null;
        if (sampleMap == null || !sampleMap.HasBeatLeaderLookup)
        {
            return false;
        }

        lock (customizationPreviewSync)
        {
            if (!customizationPreviewCaches.TryGetValue(BuildCustomizationPreviewCacheKey(sampleMap, config.LeaderboardSource), out var cache)
                || !cache.HasUsableRows)
            {
                return false;
            }

            ranked = cache.Ranked;
            stars = cache.StarRating;
            return true;
        }
    }

    public OverlayViewModel BuildCustomizationPreviewLoadingViewModel(SampleBeatmapInfo? sampleMap, bool expanded)
    {
        sampleMap ??= SampleBeatmapInfo.Fallback;
        return new OverlayViewModel
        {
            Mode = expanded ? OverlayMode.Expanded : OverlayMode.Collapsed,
            DisplayName = OverlayStateMachine.PlayerLabel,
            RankText = "--",
            MapContextText = sampleMap.DisplayName,
            MessageText = $"Loading {config.LeaderboardSource} preview...",
            IsRanked = true,
            SupportsPp = true,
            SourceName = config.LeaderboardSource,
            Rows = Array.Empty<OverlayRowViewModel>()
        };
    }

    public void RefreshCustomizationPreviewData(SampleBeatmapInfo? sampleMap, float accuracy, bool expanded)
    {
        if (sampleMap == null || !sampleMap.HasBeatLeaderLookup)
        {
            return;
        }

        var cacheKey = BuildCustomizationPreviewCacheKey(sampleMap, config.LeaderboardSource);
        var hasAllNeededData = false;
        lock (customizationPreviewSync)
        {
            if (customizationPreviewCaches.TryGetValue(cacheKey, out var cached)
                && cached.HasUsableRows
                && customizationPreviewRatingsChecked.Contains(cacheKey)
                && !ResolveCustomizationPreviewMissingPage(sampleMap, accuracy, cached).HasValue)
            {
                hasAllNeededData = true;
            }
        }

        if (hasAllNeededData)
        {
            return;
        }

        customizationPreviewCancellation?.Cancel();
        customizationPreviewCancellation?.Dispose();
        customizationPreviewCancellation = new CancellationTokenSource();
        var token = customizationPreviewCancellation.Token;
        var generation = ++customizationPreviewGeneration;

        _ = Task.Run(async () =>
        {
            try
            {
                var cache = await BuildCustomizationPreviewCacheAsync(sampleMap, accuracy, cacheKey, token).ConfigureAwait(false);
                if (cache == null || token.IsCancellationRequested)
                {
                    return;
                }

                OverlayViewModel viewModel;
                lock (customizationPreviewSync)
                {
                    viewModel = BuildCustomizationPreviewViewModel(sampleMap, accuracy, expanded, cache);
                }

                lock (customizationPreviewSync)
                {
                    if (generation != customizationPreviewGeneration)
                    {
                        return;
                    }

                    customizationPreviewCaches[cacheKey] = cache;
                    pendingCustomizationPreviewViewModel = viewModel;
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                logger.Warn("customization_preview_fetch_failed", ex.Message);
            }
        }, CancellationToken.None);
    }

    public void ClearCustomizationPreviewCache()
    {
        customizationPreviewCancellation?.Cancel();
        customizationPreviewCancellation?.Dispose();
        customizationPreviewCancellation = null;
        lock (customizationPreviewSync)
        {
            customizationPreviewCaches.Clear();
            customizationPreviewRatingsChecked.Clear();
            pendingCustomizationPreviewViewModel = null;
        }
    }

    public void ApplyPendingCustomizationPreview()
    {
        if (!customizationPreviewActive || !IsLikelyMenuScene(SceneManager.GetActiveScene().name))
        {
            lock (customizationPreviewSync)
            {
                pendingCustomizationPreviewViewModel = null;
            }

            return;
        }

        OverlayViewModel? viewModel = null;
        lock (customizationPreviewSync)
        {
            if (pendingCustomizationPreviewViewModel != null)
            {
                viewModel = pendingCustomizationPreviewViewModel;
                pendingCustomizationPreviewViewModel = null;
            }
        }

        if (viewModel != null)
        {
            UpdateCustomizationPreview(viewModel);
            CustomizationPreviewDataApplied?.Invoke();
        }
    }

    private OverlayViewModel BuildCustomizationPreviewViewModel(
        SampleBeatmapInfo sampleMap,
        float accuracy,
        bool expanded,
        LeaderboardSessionCache? sourceCache)
    {
        var clampedAccuracy = Math.Max(0f, Math.Min(100f, accuracy)) / 100d;
        var maxScore = sourceCache?.EffectiveMaxScore ?? sourceCache?.MaxScore ?? EstimateMaxScore(sampleMap.NoteCount);
        var currentScore = (int)Math.Round(maxScore * clampedAccuracy, MidpointRounding.AwayFromZero);
        var allowSyntheticRankedPreview = !sampleMap.HasBeatLeaderLookup && sampleMap.AllowRankedPreview;
        var cache = sourceCache?.HasUsableRows == true
            ? sourceCache
            : BuildSampleLeaderboardCache(sampleMap, maxScore, clampedAccuracy, allowSyntheticRankedPreview, config.LeaderboardSource);
        var pp = cache.Ranked && clampedAccuracy > 0d
            ? EstimatePreviewPp(sampleMap, clampedAccuracy, cache, currentScore)
            : 0d;
        var score = currentScore;
        var baseRank = ResolvePreviewRank(cache, pp, score, clampedAccuracy);
        var displayName = OverlayStateMachine.PlayerLabel;

        return new OverlayViewModel
        {
            Mode = expanded ? OverlayMode.Expanded : OverlayMode.Collapsed,
            DisplayName = displayName,
            RankText = "#" + baseRank.ToString(),
            MapContextText = sampleMap.DisplayName,
            ModifiersText = config.GetShowModifiers(expanded) ? BuildCustomizationPreviewSampleModifiers(cache) : string.Empty,
            IsRanked = cache.Ranked,
            SupportsPp = cache.SupportsPp,
            SourceName = config.LeaderboardSource,
            ProjectedScore = score,
            Rows = BuildCustomizationPreviewRows(sampleMap, cache, baseRank, displayName, pp, clampedAccuracy, score)
        };
    }

    private async Task<LeaderboardSessionCache?> BuildCustomizationPreviewCacheAsync(
        SampleBeatmapInfo sampleMap,
        float accuracy,
        string cacheKey,
        CancellationToken cancellationToken)
    {
        LeaderboardSessionCache cache;
        lock (customizationPreviewSync)
        {
            if (!customizationPreviewCaches.TryGetValue(cacheKey, out cache!))
            {
                cache = new LeaderboardSessionCache(sampleMap.Hash, sampleMap.Difficulty, sampleMap.Mode);
                customizationPreviewCaches[cacheKey] = cache;
            }
        }

        var needsInitialPage = false;
        lock (customizationPreviewSync)
        {
            needsInitialPage = !cache.HasUsableRows;
        }

        if (needsInitialPage)
        {
            if (!await FetchCustomizationPreviewPageAsync(cache, 1, cancellationToken).ConfigureAwait(false))
            {
                return null;
            }
        }

        if (!customizationPreviewRatingsChecked.Contains(cacheKey))
        {
            await ApplyCustomizationPreviewRatingsAsync(sampleMap, cache, cancellationToken).ConfigureAwait(false);
            lock (customizationPreviewSync)
            {
                customizationPreviewRatingsChecked.Add(cacheKey);
            }
        }

        for (var i = 0; i < MaxCustomizationPreviewPagesPerRefresh; i++)
        {
            int? pageToFetch;
            lock (customizationPreviewSync)
            {
                if (cache.FetchedPages.Count >= MaxCustomizationPreviewPagesPerMap)
                {
                    break;
                }

                pageToFetch = ResolveCustomizationPreviewMissingPage(sampleMap, accuracy, cache);
            }

            if (!pageToFetch.HasValue || pageToFetch.Value <= 0)
            {
                break;
            }

            if (!await FetchCustomizationPreviewPageAsync(cache, pageToFetch.Value, cancellationToken).ConfigureAwait(false))
            {
                break;
            }
        }

        return cache;
    }

    private int? ResolveCustomizationPreviewMissingPage(SampleBeatmapInfo sampleMap, float accuracy, LeaderboardSessionCache cache)
    {
        if (!cache.HasUsableRows)
        {
            return 1;
        }

        var clampedAccuracy = Math.Max(0f, Math.Min(100f, accuracy)) / 100d;
        if (clampedAccuracy <= 0d && cache.TotalScores > 0)
        {
            var bottomPage = cache.GetPageForRank(cache.TotalScores);
            return cache.FetchedPages.Contains(bottomPage) ? null : bottomPage;
        }

        var maxScore = cache.EffectiveMaxScore ?? cache.MaxScore ?? EstimateMaxScore(sampleMap.NoteCount);
        var currentScore = (int)Math.Round(maxScore * clampedAccuracy, MidpointRounding.AwayFromZero);
        if (currentScore <= 0)
        {
            return null;
        }

        var targetPp = cache.Ranked ? EstimatePreviewPp(sampleMap, clampedAccuracy, cache, currentScore) : 0d;
        var cachedRank = ResolveCachedPreviewRank(cache, targetPp, currentScore);
        if (cachedRank.HasValue)
        {
            var cachedRankWindow = BuildPreviewRankSelection(cachedRank.Value, Math.Max(1, config.VisiblePlayerCount));
            return cache.FindFirstMissingPageInRange(cachedRankWindow.Min(), cachedRankWindow.Max(), reverse: false);
        }

        var estimatedPage = cache.Ranked ? EstimatePreviewPageForPp(cache, targetPp) : null;
        if (estimatedPage.HasValue)
        {
            return FindNearestUnfetchedPreviewPage(cache, estimatedPage.Value);
        }

        var projection = new ProjectionEngine().Project(new BeatmapRunState
        {
            Accuracy = clampedAccuracy,
            CurrentModifiedScore = currentScore,
            KnownMaxModifiedScore = maxScore,
            ScoredNotes = sampleMap.NoteCount,
            SongProgressRatio = 1d,
            HasRecentNotes = true,
            SongTimeSeconds = sampleMap.DurationSeconds
        }, cache, useRawPpRank: true);

        var projectedRank = projection.ProjectedRank;
        if (!projectedRank.HasValue || projectedRank.Value <= 0)
        {
            return null;
        }

        var selectedRanks = BuildPreviewRankSelection(projectedRank.Value, Math.Max(1, config.VisiblePlayerCount));
        return cache.FindFirstMissingPageInRange(selectedRanks.Min(), selectedRanks.Max(), reverse: false);
    }

    private static int? ResolveCachedPreviewRank(LeaderboardSessionCache cache, double targetPp, int targetScore)
    {
        var rows = cache.ScoresByRank.Values
            .Where(row => row.Rank > 0)
            .OrderBy(row => row.Rank)
            .ToList();
        if (rows.Count == 0)
        {
            return null;
        }

        var ppRows = cache.Ranked
            ? rows.Where(row => row.Pp.GetValueOrDefault() > 0).ToList()
            : new List<BeatLeaderScoreRow>();
        if (ppRows.Count == 0)
        {
            foreach (var row in rows)
            {
                if (targetScore >= row.ModifiedScore)
                {
                    return row.Rank;
                }
            }

            return null;
        }

        var first = ppRows[0];
        if (targetPp >= first.Pp.GetValueOrDefault())
        {
            return first.Rank;
        }

        foreach (var range in BuildPreviewPagePpRanges(cache))
        {
            if (!range.Contains(targetPp))
            {
                continue;
            }

            var pageRows = ppRows
                .Where(row => cache.GetPageForRank(row.Rank) == range.Page)
                .OrderBy(row => row.Rank)
                .ToList();
            foreach (var row in pageRows)
            {
                if (targetPp >= row.Pp.GetValueOrDefault())
                {
                    return row.Rank;
                }
            }

            return pageRows.Count > 0 ? pageRows[pageRows.Count - 1].Rank + 1 : null;
        }

        return null;
    }

    private static int? EstimatePreviewPageForPp(LeaderboardSessionCache cache, double targetPp)
    {
        var ranges = BuildPreviewPagePpRanges(cache);
        if (ranges.Count == 0)
        {
            return null;
        }

        var maxPage = GetMaxPreviewPage(cache);
        if (targetPp >= ranges[0].MaxPp)
        {
            return 1;
        }

        foreach (var range in ranges)
        {
            if (range.Contains(targetPp))
            {
                return range.Page;
            }
        }

        for (var i = 0; i < ranges.Count - 1; i++)
        {
            var upper = ranges[i];
            var lower = ranges[i + 1];
            if (targetPp < upper.MinPp && targetPp > lower.MaxPp)
            {
                var pageSpan = Math.Max(1, lower.Page - upper.Page);
                var ppSpan = Math.Max(0.001d, upper.MinPp - lower.MaxPp);
                var t = Math.Max(0d, Math.Min(1d, (upper.MinPp - targetPp) / ppSpan));
                return ClampPreviewPage(upper.Page + (int)Math.Round(pageSpan * t), maxPage);
            }
        }

        var last = ranges[ranges.Count - 1];
        if (targetPp < last.MinPp)
        {
            var dropPerPage = EstimatePreviewPpDropPerPage(ranges, last);
            var extraPages = Math.Max(1, (int)Math.Ceiling((last.MinPp - targetPp) / dropPerPage));
            return ClampPreviewPage(last.Page + extraPages, maxPage);
        }

        var first = ranges[0];
        if (targetPp > first.MaxPp)
        {
            return ClampPreviewPage(first.Page - 1, maxPage);
        }

        return null;
    }

    private static int? FindNearestUnfetchedPreviewPage(LeaderboardSessionCache cache, int estimatedPage)
    {
        var maxPage = GetMaxPreviewPage(cache);
        var target = ClampPreviewPage(estimatedPage, maxPage);
        if (!cache.FetchedPages.Contains(target))
        {
            return target;
        }

        return null;
    }

    private static IReadOnlyList<PreviewPagePpRange> BuildPreviewPagePpRanges(LeaderboardSessionCache cache)
    {
        return cache.ScoresByRank.Values
            .Where(row => row.Rank > 0 && row.Pp.GetValueOrDefault() > 0)
            .GroupBy(row => cache.GetPageForRank(row.Rank))
            .Select(group =>
            {
                var values = group.Select(row => row.Pp!.Value).ToList();
                return new PreviewPagePpRange(group.Key, values.Min(), values.Max());
            })
            .OrderBy(range => range.Page)
            .ToList();
    }

    private static double EstimatePreviewPpDropPerPage(IReadOnlyList<PreviewPagePpRange> ranges, PreviewPagePpRange fallbackRange)
    {
        if (ranges.Count >= 2)
        {
            var previous = ranges[ranges.Count - 2];
            var last = ranges[ranges.Count - 1];
            var pageSpan = Math.Max(1, last.Page - previous.Page);
            var drop = Math.Max(0.001d, previous.MinPp - last.MinPp);
            return Math.Max(0.25d, drop / pageSpan);
        }

        return Math.Max(0.25d, fallbackRange.MaxPp - fallbackRange.MinPp);
    }

    private static int GetMaxPreviewPage(LeaderboardSessionCache cache)
    {
        if (cache.TotalScores <= 0)
        {
            return Math.Max(1, cache.FetchedPages.DefaultIfEmpty(1).Max() + 1);
        }

        return Math.Max(1, cache.GetPageForRank(cache.TotalScores));
    }

    private static int ClampPreviewPage(int page, int maxPage)
    {
        return Math.Max(1, Math.Min(Math.Max(1, maxPage), page));
    }

    private readonly struct PreviewPagePpRange
    {
        public PreviewPagePpRange(int page, double minPp, double maxPp)
        {
            Page = Math.Max(1, page);
            MinPp = Math.Max(0d, minPp);
            MaxPp = Math.Max(MinPp, maxPp);
        }

        public int Page { get; }

        public double MinPp { get; }

        public double MaxPp { get; }

        public bool Contains(double pp)
        {
            return pp >= MinPp && pp <= MaxPp;
        }
    }

    private async Task<bool> FetchCustomizationPreviewPageAsync(LeaderboardSessionCache cache, int page, CancellationToken cancellationToken)
    {
        lock (customizationPreviewSync)
        {
            if (cache.FetchedPages.Contains(page) || cache.FetchedPages.Count >= MaxCustomizationPreviewPagesPerMap)
            {
                return false;
            }
        }

        var result = await apiClient.GetLeaderboardScoresAsync(
            cache.Hash,
            cache.Difficulty,
            cache.Mode,
            page,
            PreviewPageSize,
            cancellationToken).ConfigureAwait(false);

        if (!result.IsSuccess || result.Value == null)
        {
            logger.Warn("customization_preview_page_fetch_failed", result.ErrorMessage ?? $"Could not fetch preview page {page}.");
            return false;
        }

        try
        {
            lock (customizationPreviewSync)
            {
                cache.ApplyPage(result.Value);
            }

            return true;
        }
        catch (ArgumentException ex)
        {
            logger.Warn("customization_preview_page_rejected", ex.Message);
            return false;
        }
    }

    private async Task ApplyCustomizationPreviewRatingsAsync(SampleBeatmapInfo sampleMap, LeaderboardSessionCache cache, CancellationToken cancellationToken)
    {
        try
        {
            var song = await apiClient.GetSongByHashAsync(sampleMap.Hash, cancellationToken).ConfigureAwait(false);
            var matched = song.Value?.Difficulties
                .FirstOrDefault(difficulty => ModeMatches(difficulty.ModeName, sampleMap.Mode) && DifficultyMatches(difficulty.DifficultyName, sampleMap.Difficulty))
                ?? song.Value?.Difficulties.FirstOrDefault(difficulty => ModeMatches(difficulty.ModeName, sampleMap.Mode));
            if (matched == null)
            {
                return;
            }

            cache.ApplyMaxScore(matched.MaxScore);
            cache.ApplyDifficultyRatings(matched.Stars, matched.PredictedAcc, matched.PassRating, matched.AccRating, matched.TechRating);
        }
        catch (Exception ex)
        {
            logger.Warn("customization_preview_ratings_fetch_failed", ex.Message);
        }
    }

    private IReadOnlyList<OverlayRowViewModel> BuildCustomizationPreviewRows(
        SampleBeatmapInfo sampleMap,
        LeaderboardSessionCache cache,
        int projectedRank,
        string displayName,
        double pp,
        double accuracy,
        int score)
    {
        var targetRows = Math.Max(0, config.VisiblePlayerCount);
        if (targetRows == 0)
        {
            return Array.Empty<OverlayRowViewModel>();
        }

        var selectedRanks = BuildPreviewRankSelection(projectedRank, targetRows);
        var rows = cache.GetNearbyRows(projectedRank, targetRows + 4)
            .Select(row => ToCustomizationPreviewRow(sampleMap, cache, row, row.Rank >= projectedRank ? row.Rank + 1 : row.Rank))
            .Where(row => selectedRanks.Contains(row.Rank))
            .ToList();

        if (rows.Count < targetRows - 1)
        {
            rows.AddRange(cache.ScoresByRank.Values
                .Select(row => ToCustomizationPreviewRow(sampleMap, cache, row, row.Rank >= projectedRank ? row.Rank + 1 : row.Rank))
                .Where(row => selectedRanks.Contains(row.Rank))
                .Where(row => rows.All(existing => !string.Equals(existing.PlayerId, row.PlayerId, StringComparison.OrdinalIgnoreCase)))
                .OrderBy(row => Math.Abs(row.Rank - projectedRank))
                .Take(targetRows));
        }

        rows.Add(new OverlayRowViewModel
        {
            Rank = projectedRank,
            PlayerName = displayName,
            ValueText = FormatCustomizationPreviewValue(cache, pp, score),
            Modifiers = BuildCustomizationPreviewSampleModifiers(cache),
            Accuracy = accuracy,
            Score = score,
            IsLocalPlayer = true,
            IsProjected = true
        });

        foreach (var rank in selectedRanks)
        {
            if (rank == projectedRank || rows.Any(row => row.Rank == rank))
            {
                continue;
            }

            rows.Add(new OverlayRowViewModel
            {
                Rank = rank,
                PlayerName = BuildPreviewGenericPlayerName(rank),
                ValueText = cache.Ranked ? "--" : string.Empty,
                IsLocalPlayer = false,
                IsProjected = true
            });
        }

        return rows
            .GroupBy(row => row.IsLocalPlayer ? "__local" : (!string.IsNullOrWhiteSpace(row.PlayerId) ? row.PlayerId : row.PlayerName), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(row => row.Rank)
            .Take(targetRows)
            .ToList();
    }

    private static OverlayRowViewModel ToCustomizationPreviewRow(SampleBeatmapInfo sampleMap, LeaderboardSessionCache cache, BeatLeaderScoreRow row, int displayRank)
    {
        var rowPp = row.Pp.GetValueOrDefault();
        if (cache.Ranked && (!sampleMap.HasBeatLeaderLookup || rowPp <= 0d))
        {
            var accuracy = NormalizeAccuracy(row.Accuracy);
            rowPp = cache.UsesScoreSaberPpCurve
                ? ScoreSaberPpCalculator.Calculate(cache.StarRating ?? EstimateSampleStars(sampleMap), accuracy)
                : cache.HasBeatLeaderRatings || !sampleMap.HasBeatLeaderLookup
                    ? EstimatePreviewPp(sampleMap, accuracy, cache, row.ModifiedScore)
                    : rowPp;
        }

        return new OverlayRowViewModel
        {
            Rank = displayRank,
            PlayerId = row.PlayerId,
            PlayerName = SanitizeCustomizationPreviewPlayerName(row.PlayerName, displayRank, sampleMap.HasBeatLeaderLookup),
            ValueText = FormatCustomizationPreviewValue(cache, rowPp, row.ModifiedScore),
            Accuracy = row.Accuracy,
            Score = row.ModifiedScore > 0 ? row.ModifiedScore : null,
            IsLocalPlayer = false,
            IsProjected = false
        };
    }

    private static string SanitizeCustomizationPreviewPlayerName(string? playerName, int displayRank, bool allowRealLeaderboardName)
    {
        if (allowRealLeaderboardName && !string.IsNullOrWhiteSpace(playerName))
        {
            return playerName.Trim();
        }

        if (string.Equals(playerName, "darkrelicer", StringComparison.OrdinalIgnoreCase))
        {
            return "darkrelicer";
        }

        if (string.Equals(playerName, "built with Codex", StringComparison.OrdinalIgnoreCase))
        {
            return "built with Codex";
        }

        return BuildPreviewGenericPlayerName(displayRank);
    }

    private static string BuildPreviewGenericPlayerName(int rank)
    {
        return "Player " + Math.Max(1, rank).ToString(CultureInfo.InvariantCulture);
    }

    private static string FormatCustomizationPreviewValue(LeaderboardSessionCache cache, double pp, int score)
    {
        if (!cache.Ranked)
        {
            return string.Empty;
        }

        return pp > 0d
            ? pp.ToString("0.0", CultureInfo.InvariantCulture) + "pp"
            : "--";
    }

    private static string BuildCustomizationPreviewSampleModifiers(LeaderboardSessionCache cache)
    {
        return string.Equals(cache.SourceName, "ScoreSaber", StringComparison.OrdinalIgnoreCase)
            ? "SF"
            : "BE";
    }

    private static IReadOnlyList<int> BuildPreviewRankSelection(int projectedRank, int count)
    {
        if (count <= 0)
        {
            return Array.Empty<int>();
        }

        var rows = new List<int>();
        var above = Math.Max(0, count / 2);
        var start = Math.Max(1, projectedRank - above);
        var missingAbove = Math.Max(0, above - (projectedRank - start));
        while (rows.Count < count)
        {
            rows.Add(start + rows.Count);
        }

        if (!rows.Contains(projectedRank))
        {
            rows[rows.Count - 1] = projectedRank;
            rows.Sort();
        }

        return rows;
    }

    private static int ResolvePreviewRank(LeaderboardSessionCache cache, double pp, int score, double accuracy)
    {
        var rows = cache.ScoresByRank.Values.OrderBy(row => row.Rank).ToList();
        if (rows.Count == 0)
        {
            return 1;
        }

        if (accuracy <= 0d && cache.TotalScores > 0)
        {
            return cache.TotalScores + 1;
        }

        foreach (var row in rows)
        {
            var rowPp = row.Pp.GetValueOrDefault();
            if (cache.Ranked && rowPp > 0 && pp >= rowPp)
            {
                return row.Rank;
            }

            if ((!cache.Ranked || rowPp <= 0) && score >= row.ModifiedScore)
            {
                return row.Rank;
            }
        }

        var fallback = rows[rows.Count - 1].Rank + 1;
        return cache.TotalScores > 0 ? Math.Min(fallback, cache.TotalScores + 1) : fallback;
    }

    private static string BuildCustomizationPreviewCacheKey(SampleBeatmapInfo sampleMap, string leaderboardSource)
    {
        var source = string.IsNullOrWhiteSpace(leaderboardSource) ? "BeatLeader" : leaderboardSource.Trim();
        return $"{source}:{sampleMap.Hash}:{sampleMap.Difficulty}:{sampleMap.Mode}";
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

    private static LeaderboardSessionCache BuildSampleLeaderboardCache(
        SampleBeatmapInfo sampleMap,
        int maxScore,
        double targetAccuracy,
        bool allowRankedPreview,
        string leaderboardSource)
    {
        var estimatedStars = EstimateSampleStars(sampleMap);
        var useScoreSaberCurve = string.Equals(leaderboardSource, "ScoreSaber", StringComparison.OrdinalIgnoreCase);
        var sourceName = useScoreSaberCurve ? "ScoreSaber" : "BeatLeader";
        var cache = new LeaderboardSessionCache(sampleMap.Key, sampleMap.Difficulty, "Standard");
        cache.ApplyPage(new LeaderboardScoresResponse
        {
            Metadata = new LeaderboardMetadata
            {
                Page = 1,
                ItemsPerPage = 100,
                Total = 5000
            },
            Container = new LeaderboardContainer
            {
                LeaderboardId = "sample-" + Math.Abs(sampleMap.Key.GetHashCode()).ToString(),
                Ranked = allowRankedPreview,
                MaxScore = maxScore,
                Stars = allowRankedPreview ? estimatedStars : null,
                SourceName = sourceName,
                RankByPp = !useScoreSaberCurve,
                SupportsPp = allowRankedPreview,
                UsesScoreSaberPpCurve = useScoreSaberCurve
            },
            Data = BuildSampleRows(maxScore, targetAccuracy, sampleMap, estimatedStars, allowRankedPreview, useScoreSaberCurve)
        });

        if (allowRankedPreview)
        {
            var (passRating, accRating, techRating) = ResolveSampleBeatLeaderRatings(sampleMap, estimatedStars);
            cache.ApplyDifficultyRatings(estimatedStars, 0.965d, passRating, accRating, techRating);
        }

        return cache;
    }

    private static List<BeatLeaderScoreDto> BuildSampleRows(
        int maxScore,
        double targetAccuracy,
        SampleBeatmapInfo sampleMap,
        double estimatedStars,
        bool allowRankedPreview,
        bool useScoreSaberCurve)
    {
        var rows = new List<BeatLeaderScoreDto>();
        const int totalRows = 5000;
        for (var i = 0; i < totalRows; i++)
        {
            var rank = i + 1;
            var topAccuracy = Math.Min(0.9999d, 0.992d + (estimatedStars * 0.00045d));
            var floorAccuracy = 0.015d;
            var t = totalRows <= 1 ? 0d : (double)i / (totalRows - 1);
            var rowAccuracy = Math.Max(floorAccuracy, topAccuracy - (Math.Pow(t, 0.593d) * (topAccuracy - floorAccuracy)));
            var score = Math.Max(1, (int)Math.Round(maxScore * rowAccuracy, MidpointRounding.AwayFromZero));
            var pp = allowRankedPreview
                ? useScoreSaberCurve
                    ? ScoreSaberPpCalculator.Calculate(estimatedStars, rowAccuracy)
                    : EstimatePreviewPp(sampleMap, rowAccuracy, estimatedStars)
                : 0d;
            rows.Add(new BeatLeaderScoreDto
            {
                Id = rank,
                Rank = rank,
                BaseScore = score,
                ModifiedScore = score,
                Accuracy = rowAccuracy,
                Pp = pp,
                Modifiers = allowRankedPreview
                    ? useScoreSaberCurve
                        ? rank % 7 == 0 ? "SF" : string.Empty
                        : rank % 7 == 0 ? "SF,BE" : rank % 3 == 0 ? "BE" : string.Empty
                    : string.Empty,
                Player = new BeatLeaderPlayerDto
                {
                    Id = "sample-" + rank.ToString(),
                    Name = rank switch
                    {
                        1 => "darkrelicer",
                        2 => "built with Codex",
                        _ => "Player " + rank.ToString()
                    }
                }
            });
        }

        return rows;
    }

    private static int EstimateMaxScore(int noteCount)
    {
        noteCount = Math.Max(1, noteCount);
        if (noteCount == 1)
        {
            return 115;
        }

        if (noteCount <= 5)
        {
            return 115 * (1 + ((noteCount - 1) * 2));
        }

        if (noteCount <= 13)
        {
            return 115 * (1 + (4 * 2) + ((noteCount - 5) * 4));
        }

        return 115 * (1 + (4 * 2) + (8 * 4) + ((noteCount - 13) * 8));
    }

    private static double EstimateSampleStars(SampleBeatmapInfo sampleMap)
    {
        var durationMinutes = Math.Max(0.5d, sampleMap.DurationSeconds / 60d);
        var notesPerSecond = sampleMap.NoteCount / Math.Max(1d, sampleMap.DurationSeconds);
        var difficultyBonus = sampleMap.Difficulty.IndexOf("ExpertPlus", StringComparison.OrdinalIgnoreCase) >= 0 ? 2.8d
            : sampleMap.Difficulty.IndexOf("Expert", StringComparison.OrdinalIgnoreCase) >= 0 ? 1.6d
            : sampleMap.Difficulty.IndexOf("Hard", StringComparison.OrdinalIgnoreCase) >= 0 ? 0.7d
            : 0.2d;
        var density = Math.Pow(Math.Max(0.1d, notesPerSecond), 1.18d) * 1.35d;
        var lengthBonus = Math.Min(1.4d, durationMinutes * 0.22d);
        return Math.Max(1d, Math.Min(16d, density + difficultyBonus + lengthBonus));
    }

    private static double EstimatePreviewPp(SampleBeatmapInfo sampleMap, double accuracy, LeaderboardSessionCache cache, int? score = null)
    {
        if (!cache.Ranked)
        {
            return 0d;
        }

        var estimatedStars = cache.StarRating ?? EstimateSampleStars(sampleMap);
        if (cache.UsesScoreSaberPpCurve)
        {
            return ScoreSaberPpCalculator.Calculate(estimatedStars, accuracy);
        }

        if (cache.HasBeatLeaderRatings || !sampleMap.HasBeatLeaderLookup)
        {
            var sampleRatings = ResolveSampleBeatLeaderRatings(sampleMap, estimatedStars);
            return EstimatePreviewPp(
                accuracy,
                cache.PassRating ?? sampleRatings.PassRating,
                cache.AccRating ?? sampleRatings.AccRating,
                cache.TechRating ?? sampleRatings.TechRating);
        }

        if (TryEstimatePreviewPpFromLeaderboard(accuracy, score, cache, out var leaderboardPp))
        {
            return leaderboardPp;
        }

        var fallbackRatings = ResolveSampleBeatLeaderRatings(sampleMap, estimatedStars);
        return EstimatePreviewPp(
            accuracy,
            cache.PassRating ?? fallbackRatings.PassRating,
            cache.AccRating ?? fallbackRatings.AccRating,
            cache.TechRating ?? fallbackRatings.TechRating);
    }

    private static bool TryEstimatePreviewPpFromLeaderboard(double accuracy, int? score, LeaderboardSessionCache cache, out double pp)
    {
        pp = 0d;
        if (!cache.Ranked)
        {
            return false;
        }

        var rows = cache.ScoresByRank.Values
            .Where(row => row.Pp.GetValueOrDefault() > 0)
            .Select(row => new PreviewPpAnchor(
                NormalizeAccuracy(row.Accuracy),
                row.ModifiedScore,
                row.Pp.GetValueOrDefault()))
            .Where(anchor => anchor.Pp > 0 && (anchor.Accuracy > 0 || anchor.Score > 0))
            .OrderByDescending(anchor => anchor.Accuracy > 0 ? anchor.Accuracy : double.NegativeInfinity)
            .ThenByDescending(anchor => anchor.Score)
            .ToList();
        if (rows.Count == 0)
        {
            return false;
        }

        var targetAccuracy = Clamp01(accuracy);
        if (targetAccuracy <= 0d)
        {
            pp = 0d;
            return true;
        }

        var accuracyRows = rows.Where(anchor => anchor.Accuracy > 0).ToList();
        if (accuracyRows.Count > 0)
        {
            pp = InterpolatePreviewPpByAccuracy(targetAccuracy, accuracyRows);
            return true;
        }

        if (score.GetValueOrDefault() <= 0)
        {
            return false;
        }

        pp = InterpolatePreviewPpByScore(score.Value, rows.OrderByDescending(anchor => anchor.Score).ToList());
        return true;
    }

    private static double InterpolatePreviewPpByAccuracy(double accuracy, IReadOnlyList<PreviewPpAnchor> rows)
    {
        if (rows.Count == 1)
        {
            return ExtrapolatePreviewPpBelowLowestAccuracy(accuracy, rows[0]);
        }

        if (accuracy >= rows[0].Accuracy)
        {
            return ExtrapolatePreviewPpByAccuracy(accuracy, rows[0], rows[1]);
        }

        var lastIndex = rows.Count - 1;
        if (accuracy <= rows[lastIndex].Accuracy)
        {
            return ExtrapolatePreviewPpBelowLowestAccuracy(accuracy, rows[lastIndex]);
        }

        for (var i = 0; i < rows.Count - 1; i++)
        {
            var high = rows[i];
            var low = rows[i + 1];
            if (accuracy <= high.Accuracy && accuracy >= low.Accuracy)
            {
                var denominator = high.Accuracy - low.Accuracy;
                if (denominator <= 0d)
                {
                    return Math.Max(0d, low.Pp);
                }

                var t = (accuracy - low.Accuracy) / denominator;
                return Math.Max(0d, low.Pp + ((high.Pp - low.Pp) * t));
            }
        }

        return Math.Max(0d, rows[lastIndex].Pp);
    }

    private static double InterpolatePreviewPpByScore(int score, IReadOnlyList<PreviewPpAnchor> rows)
    {
        if (rows.Count == 1)
        {
            return ExtrapolatePreviewPpBelowLowestScore(score, rows[0]);
        }

        if (score >= rows[0].Score)
        {
            return ExtrapolatePreviewPpByScore(score, rows[0], rows[1]);
        }

        var lastIndex = rows.Count - 1;
        if (score <= rows[lastIndex].Score)
        {
            return ExtrapolatePreviewPpBelowLowestScore(score, rows[lastIndex]);
        }

        for (var i = 0; i < rows.Count - 1; i++)
        {
            var high = rows[i];
            var low = rows[i + 1];
            if (score <= high.Score && score >= low.Score)
            {
                var denominator = high.Score - low.Score;
                if (denominator <= 0)
                {
                    return Math.Max(0d, low.Pp);
                }

                var t = (double)(score - low.Score) / denominator;
                return Math.Max(0d, low.Pp + ((high.Pp - low.Pp) * t));
            }
        }

        return Math.Max(0d, rows[lastIndex].Pp);
    }

    private static double ExtrapolatePreviewPpBelowLowestAccuracy(double accuracy, PreviewPpAnchor lowest)
    {
        if (lowest.Accuracy <= 0d || lowest.Pp <= 0d || accuracy <= 0d)
        {
            return 0d;
        }

        const double lowAccuracyPower = 7d;
        var ratio = Clamp01(accuracy / lowest.Accuracy);
        return Math.Max(0d, lowest.Pp * Math.Pow(ratio, lowAccuracyPower));
    }

    private static double ExtrapolatePreviewPpBelowLowestScore(int score, PreviewPpAnchor lowest)
    {
        if (lowest.Score <= 0 || lowest.Pp <= 0d || score <= 0)
        {
            return 0d;
        }

        const double lowScorePower = 7d;
        var ratio = Clamp01(score / (double)lowest.Score);
        return Math.Max(0d, lowest.Pp * Math.Pow(ratio, lowScorePower));
    }

    private static double ExtrapolatePreviewPpByAccuracy(double accuracy, PreviewPpAnchor high, PreviewPpAnchor low)
    {
        var denominator = high.Accuracy - low.Accuracy;
        if (Math.Abs(denominator) <= 0.000001d)
        {
            return Math.Max(0d, low.Pp);
        }

        var slope = (high.Pp - low.Pp) / denominator;
        return Math.Max(0d, low.Pp + ((accuracy - low.Accuracy) * slope));
    }

    private static double ExtrapolatePreviewPpByScore(int score, PreviewPpAnchor high, PreviewPpAnchor low)
    {
        var denominator = high.Score - low.Score;
        if (denominator == 0)
        {
            return Math.Max(0d, low.Pp);
        }

        var slope = (high.Pp - low.Pp) / denominator;
        return Math.Max(0d, low.Pp + ((score - low.Score) * slope));
    }

    private static double NormalizeAccuracy(double? accuracy)
    {
        var value = accuracy.GetValueOrDefault();
        if (value <= 0d)
        {
            return 0d;
        }

        return value > 1.0001d ? Clamp01(value / 100d) : Clamp01(value);
    }

    private static double Clamp01(double value)
    {
        if (value < 0d)
        {
            return 0d;
        }

        if (value > 1d)
        {
            return 1d;
        }

        return value;
    }

    private readonly struct PreviewPpAnchor
    {
        public PreviewPpAnchor(double accuracy, int score, double pp)
        {
            Accuracy = accuracy;
            Score = Math.Max(0, score);
            Pp = Math.Max(0d, pp);
        }

        public double Accuracy { get; }

        public int Score { get; }

        public double Pp { get; }
    }

    private static double EstimatePreviewPp(SampleBeatmapInfo sampleMap, double accuracy, double estimatedStars)
    {
        var (passRating, accRating, techRating) = ResolveSampleBeatLeaderRatings(sampleMap, estimatedStars);
        return EstimatePreviewPp(
            accuracy,
            passRating,
            accRating,
            techRating);
    }

    private static (double PassRating, double AccRating, double TechRating) ResolveSampleBeatLeaderRatings(SampleBeatmapInfo sampleMap, double stars)
    {
        if (sampleMap.HasBeatLeaderRatingComponents)
        {
            return (
                sampleMap.BeatLeaderPassRating.GetValueOrDefault(),
                sampleMap.BeatLeaderAccRating.GetValueOrDefault(),
                sampleMap.BeatLeaderTechRating.GetValueOrDefault());
        }

        var rating = Math.Max(0d, stars);
        return (rating, rating, rating);
    }

    private static double EstimatePreviewPp(double accuracy, double passRating, double accRating, double techRating)
    {
        if (accuracy <= 0d)
        {
            return 0d;
        }

        var accCurve = BeatLeaderSampleAccCurveValue(accuracy);
        var passPp = Math.Max(0d, (15.2d * Math.Exp(Math.Pow(passRating, 1d / 2.62d))) - 30d);
        var accPp = accCurve * accRating * 34d;
        var techPp = Math.Exp(1.9d * accuracy) * 1.08d * techRating;
        var rawPp = passPp + accPp + techPp;
        const double scale = 650d;
        return rawPp <= 0d ? 0d : (scale * Math.Pow(rawPp, 1.3d)) / Math.Pow(scale, 1.3d);
    }

    private static double BeatLeaderSampleAccCurveValue(double accuracy)
    {
        var clamped = Clamp01(accuracy);
        for (var i = 1; i < BeatLeaderSampleAccCurve.Length; i++)
        {
            var previous = BeatLeaderSampleAccCurve[i - 1];
            var current = BeatLeaderSampleAccCurve[i];
            if (clamped > previous.Accuracy || clamped < current.Accuracy)
            {
                continue;
            }

            var denominator = current.Accuracy - previous.Accuracy;
            if (Math.Abs(denominator) <= 0.0000001d)
            {
                return current.Value;
            }

            var t = (clamped - previous.Accuracy) / denominator;
            return previous.Value + (t * (current.Value - previous.Value));
        }

        return BeatLeaderSampleAccCurve[BeatLeaderSampleAccCurve.Length - 1].Value;
    }

    private static double EstimatePpStep(SampleBeatmapInfo sampleMap, int distance)
    {
        var stars = EstimateSampleStars(sampleMap);
        return Math.Max(0.4d, stars * distance * 0.18d);
    }

    public void SetMenuPreviewVisible(bool visible)
    {
        EnsureOverlayInitialized();
        if (!visible)
        {
            overlayRenderer?.SetCustomizationPreviewActive(false);
            overlayRenderer?.SetWorldVisible(IsLikelyGameplayScene(SceneManager.GetActiveScene().name) && config.Enabled && !hudSuppressedByGame && sessionActive && !isPaused);
            overlayRenderer?.SetGameplayActive(IsLikelyGameplayScene(SceneManager.GetActiveScene().name) && sessionActive);
            return;
        }

        SetCustomizationPreviewVisible(false);
    }

    public void HandleGameplayExit(string reason)
    {
        ClearCustomizationPreviewCache();
        EndActiveSession(reason);
    }

    private bool TryResumeRestartedSession(object scoreController)
    {
        if (scoreController == null || sessionStartupInFlight || lastDetectedBeatmap == null)
        {
            return false;
        }

        if (!IsLikelyGameplayScene(SceneManager.GetActiveScene().name))
        {
            return false;
        }

        logger.Info("runtime_restart_session_resuming", "ScoreController updated while no overlay session was active; restarting from last detected beatmap.");
        StartDetectedSong(CloneBeatmapSessionInfo(lastDetectedBeatmap));
        return true;
    }

    private async Task StartDetectedSongAsync(BeatmapSessionInfo beatmap, CancellationToken cancellationToken)
    {
        // Gameplay hooks enter on Unity's main thread. Retain its synchronization
        // context across I/O before touching renderer objects or runtime state.
        try
        {
            if (!IsCurrentSession(cancellationToken)) return;
            string? resolvedPlayerId = null;
            if (!beatmap.IsReplayMode)
            {
                if (IsScoreSaberMode())
                {
                    resolvedPlayerId = TryResolveScoreSaberPlayerId();
                }
                else
                {
                    var oauthIdentity = await TryResolveBeatLeaderOAuthIdentityAsync(cancellationToken).ConfigureAwait(true);
                    if (!IsCurrentSession(cancellationToken)) return;
                    if (!string.IsNullOrWhiteSpace(oauthIdentity?.Id))
                    {
                        resolvedPlayerId = oauthIdentity!.Id!.Trim();
                    }

                    if (string.IsNullOrWhiteSpace(resolvedPlayerId))
                    {
                        resolvedPlayerId = TryResolveBeatLeaderPlayerIdFromLocalMetadata();
                    }
                }
            }

            var result = await sessionManager.StartAsync(
                beatmap,
                resolvedPlayerId,
                cancellationToken).ConfigureAwait(true);

            if (!IsCurrentSession(cancellationToken)) return;

            logger.Info("runtime_session_start_result", result.IsStarted ? $"{apiClient.SourceName} session started." : result.ErrorMessage ?? $"{apiClient.SourceName} session did not start.");

            if (!result.IsStarted)
            {
                lastDetectedBeatmap = null;
                SetRendererViewModel(OverlayViewModel.Unavailable(result.ErrorMessage ?? $"{apiClient.SourceName} leaderboard unavailable."));
                overlayRenderer?.SetWorldVisible(false);
                overlayRenderer?.SetGameplayActive(false);
                overlayRenderer?.SetHudSuppressed(false);
                return;
            }

            sessionActive = true;
            overlayRenderer?.SetGameplayActive(true);
            overlayRenderer?.SetReplayModeRenderingEnabled(activeReplayMode);
            overlayRenderer?.SetHudSuppressed(hudSuppressedByGame);
            var update = sessionManager.Update(new BeatmapRunState
            {
                CurrentScore = 0,
                CurrentModifiedScore = 0,
                SongProgressRatio = 0,
                HasRecentNotes = true,
                SongTimeSeconds = 0,
                IsReplayMode = activeReplayMode,
                IsFailedWithNoFail = levelFailedWithNoFail,
                ActiveModifiers = activeModifiers
            });
            if (activeReplayMode)
            {
                update.ViewModel.Mode = OverlayMode.Expanded;
            }

            SetRendererViewModel(update.ViewModel);
            overlayRenderer?.SetWorldVisible(config.Enabled && !hudSuppressedByGame);
            logger.Info("runtime_overlay_snapshot", FormatViewModel(update.ViewModel));
        }
        catch (OperationCanceledException)
        {
            logger.Info("runtime_session_cancelled", "Cancelled previous BeatLeader session startup.");
        }
        catch (Exception ex)
        {
            if (!IsCurrentSession(cancellationToken)) return;
            lastDetectedBeatmap = null;
            logger.Error("runtime_session_start_failed", ex.ToString());
        }
        finally
        {
            if (IsCurrentSession(cancellationToken))
            {
                sessionStartupInFlight = false;
            }
        }
    }

    private bool IsCurrentSession(CancellationToken token)
    {
        return !token.IsCancellationRequested
            && sessionCancellation != null
            && sessionCancellation.Token == token;
    }

    private bool IsScoreSaberMode()
    {
        return string.Equals(config.LeaderboardSource, "ScoreSaber", StringComparison.OrdinalIgnoreCase);
    }

    private string? TryResolveScoreSaberPlayerId()
    {
        var configuredPlayerId = Environment.GetEnvironmentVariable("SCORESABER_PLAYER_ID");
        if (IsLikelyScoreSaberPlayerId(configuredPlayerId))
        {
            logger.Info("runtime_scoresaber_player_id_resolved", "Resolved ScoreSaber player id from SCORESABER_PLAYER_ID.");
            return configuredPlayerId!.Trim();
        }

        var localPlatformId = TryResolveBeatSaberPlatformUserId();
        if (IsLikelyScoreSaberPlayerId(localPlatformId))
        {
            logger.Info("runtime_scoresaber_player_id_resolved", "Resolved ScoreSaber player id from local Beat Saber platform user id.");
            return localPlatformId!.Trim();
        }

        return null;
    }

    private string? TryResolveBeatSaberPlatformUserId()
    {
        try
        {
            var candidates = new List<object?>();
            foreach (var typeName in new[] { "PlayerDataModel", "PlatformUserModel", "UserInfo" })
            {
                var type = AppDomain.CurrentDomain
                    .GetAssemblies()
                    .Select(assembly => assembly.GetType(typeName, throwOnError: false, ignoreCase: false))
                    .FirstOrDefault(type => type != null);
                if (type == null)
                {
                    continue;
                }

                if (!typeof(UnityEngine.Object).IsAssignableFrom(type))
                {
                    continue;
                }

                var objects = UnityEngine.Resources.FindObjectsOfTypeAll(type);
                if (objects != null)
                {
                    candidates.AddRange(objects.Cast<object?>());
                }
            }

            foreach (var candidate in candidates)
            {
                var userId = ReadStringDeep(candidate, maxDepth: 3, new HashSet<object>(), "userId", "_userId", "platformUserId", "_platformUserId", "platformId", "_platformId");
                if (IsLikelyScoreSaberPlayerId(userId))
                {
                    return userId!.Trim();
                }
            }
        }
        catch (Exception ex)
        {
            logger.Warn("runtime_scoresaber_player_id_unavailable", ex.Message);
        }

        return null;
    }

    private static bool IsLikelyScoreSaberPlayerId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var trimmed = value.Trim();
        return trimmed.Length >= 8
            && trimmed.Length <= 32
            && trimmed.All(char.IsDigit);
    }

    private async Task<BeatLeaderOAuthIdentityDto?> TryResolveBeatLeaderOAuthIdentityAsync(CancellationToken cancellationToken)
    {
        var accessToken = ResolveBeatLeaderAccessToken();
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            return null;
        }

        try
        {
            var result = await sessionManager.ApiClient.GetOAuthIdentityAsync(accessToken!, cancellationToken).ConfigureAwait(false);
            if (!result.IsSuccess || result.Value == null)
            {
                return null;
            }

            return result.Value;
        }
        catch (Exception ex)
        {
            logger.Warn("runtime_oauth_identity_failed", ex.Message);
            return null;
        }
    }

    private static string? ResolveBeatLeaderAccessToken()
    {
        var token = Environment.GetEnvironmentVariable("BEATLEADER_ACCESS_TOKEN");
        return string.IsNullOrWhiteSpace(token) ? null : token.Trim();
    }

    private string? TryResolveBeatLeaderPlayerIdFromLocalMetadata()
    {
        var configuredPlayerId = Environment.GetEnvironmentVariable("BEATLEADER_PLAYER_ID");
        if (IsLikelyBeatLeaderPlayerId(configuredPlayerId))
        {
            return configuredPlayerId!.Trim();
        }

        try
        {
            var replaysDirectory = Path.Combine(GetBeatSaberRootDirectory(), "UserData", "BeatLeader", "Replays");
            if (!Directory.Exists(replaysDirectory))
            {
                return null;
            }

            var candidate = Directory.EnumerateFiles(replaysDirectory, "*.bsor", SearchOption.TopDirectoryOnly)
                .Select(path => new FileInfo(path))
                .Where(file => file.Exists)
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .Select(file => BeatLeaderReplayPlayerIdPattern.Match(file.Name))
                .Where(match => match.Success)
                .Select(match => match.Groups["id"].Value)
                .FirstOrDefault(IsLikelyBeatLeaderPlayerId);

            if (!string.IsNullOrWhiteSpace(candidate))
            {
                logger.Info("runtime_local_player_id_resolved", "Resolved BeatLeader player id from local replay metadata.");
                return candidate.Trim();
            }
        }
        catch (Exception ex)
        {
            logger.Warn("runtime_local_player_id_unavailable", ex.Message);
        }

        return null;
    }

    private static bool IsLikelyBeatLeaderPlayerId(string? value)
    {
        return !string.IsNullOrWhiteSpace(value)
            && value.Trim().Length >= 16
            && value.Trim().Length <= 20
            && value.Trim().All(char.IsDigit);
    }

    private static string GetBeatSaberRootDirectory()
    {
        var pluginPath = Assembly.GetExecutingAssembly().Location;
        var pluginDirectory = Path.GetDirectoryName(pluginPath);
        if (!string.IsNullOrWhiteSpace(pluginDirectory))
        {
            var directory = new DirectoryInfo(pluginDirectory);
            return directory.Parent?.FullName ?? directory.FullName;
        }

        return Environment.CurrentDirectory;
    }

    private async Task FetchSuggestedPageAsync(OverlayUpdateResult update)
    {
        if (pageFetchInFlight || sessionCancellation == null || !update.PageToFetch.HasValue)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        if (now - lastRuntimePageFetchUtc < TimeSpan.FromSeconds(ResolveRuntimePageFetchCooldownSeconds()))
        {
            return;
        }

        var token = sessionCancellation.Token;
        lastRuntimePageFetchUtc = now;
        pageFetchInFlight = true;
        try
        {
            await sessionManager.TryFetchSuggestedPageAsync(update, token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (IsCurrentSession(token)) logger.Warn("runtime_page_fetch_failed", ex.Message);
        }
        finally
        {
            if (IsCurrentSession(token)) pageFetchInFlight = false;
        }
    }

    private double ResolveRuntimePageFetchCooldownSeconds()
    {
        var updateInterval = Math.Max(0.25d, Math.Min(3.0d, config.UpdateIntervalSeconds));
        return IsScoreSaberMode()
            ? Math.Max(1.0d, updateInterval)
            : updateInterval;
    }

    private BeatmapRunState BuildRunState(object scoreController, DateTimeOffset now)
    {
        EnsureRuntimeReferences(scoreController);
        var audioTimeSyncController = cachedAudioTimeSyncController;
        var songTime = ReadFloat(audioTimeSyncController, "songTime", "_songTime") ?? 0f;
        lastKnownSongTime = songTime;
        var songLength = ReadFloat(audioTimeSyncController, "songLength") ?? 0f;
        if (songLength > 0 && !activeSongLengthSeconds.HasValue)
        {
            activeSongLengthSeconds = songLength;
        }

        if (!activeReplayMode && !runtimeBeatmapTimelineLoadAttempted)
        {
            EnsureRuntimeTimelineLoaded(scoreController);
        }
        var progress = songLength > 0 ? Math.Max(0, Math.Min(1, songTime / songLength)) : 0;
        var currentScore = ReadInt(scoreController, "multipliedScore", "_multipliedScore") ?? 0;
        var currentModifiedScore = ReadInt(scoreController, "modifiedScore", "_modifiedScore") ?? 0;
        if (!levelFailedWithNoFail && ShouldInferNoFailPenaltyFromScore(currentScore, currentModifiedScore))
        {
            MarkLevelFailed();
        }

        var immediateMaxPossible = ReadInt(
            scoreController,
            "immediateMaxPossibleModifiedScore",
            "_immediateMaxPossibleModifiedScore",
            "immediateMaxPossibleMultipliedScore",
            "_immediateMaxPossibleMultipliedScore");
        var immediateMaxPossibleValue = immediateMaxPossible.GetValueOrDefault();
        var liveAccuracy = immediateMaxPossibleValue > 0
            ? Math.Max(0, Math.Min(1, (double)currentModifiedScore / immediateMaxPossibleValue))
            : 0;
        var fallbackTimelineBreak = activeReplayMode ? BreakTimelineState.Collapsed : TryResolveTimelineBreak(songTime);
        var activeTimelineBreak = fallbackTimelineBreak;
        var secondsUntilNextScorableNote = activeReplayMode ? double.PositiveInfinity : GetSecondsUntilNextScorableNote(songTime);
        var hasUpcomingScorableNote = !double.IsPositiveInfinity(secondsUntilNextScorableNote);

        return new BeatmapRunState
        {
            CurrentScore = currentScore,
            CurrentModifiedScore = currentModifiedScore,
            Accuracy = liveAccuracy,
            SongProgressRatio = progress,
            ScoredNotes = 0,
            MissCount = 0,
            BadCutCount = 0,
            IsPaused = isPaused,
            IsReplayMode = activeReplayMode,
            HasRecentNotes = false,
            SecondsSinceLastNote = 999,
            SongTimeSeconds = songTime,
            IsInPlannedBreak = activeTimelineBreak.IsExpanded,
            IsAfterLastNote = IsAfterLastNote(songTime),
            HasUpcomingScorableNote = hasUpcomingScorableNote,
            SecondsUntilNextScorableNote = secondsUntilNextScorableNote,
            BreakWindowStartTimeSeconds = activeTimelineBreak.StartTime,
            BreakWindowEndTimeSeconds = activeTimelineBreak.EndTime,
            HasReliableRuntimeBreakDetector = activeBreakWindows.Count > 0,
            IsFailedWithNoFail = levelFailedWithNoFail,
            ActiveModifiers = activeModifiers
        };
    }

    private bool ShouldInferNoFailPenaltyFromScore(int multipliedScore, int modifiedScore)
    {
        if (!BeatLeaderModifierPolicy.HasNoFail(activeModifiers) || multipliedScore <= 0 || modifiedScore <= 0)
        {
            return false;
        }

        var expectedWithoutNoFail = ResolveBaseGameModifierMultiplier(activeModifiers, includeNoFail: false);
        var expectedWithNoFail = ResolveBaseGameModifierMultiplier(activeModifiers, includeNoFail: true);
        if (Math.Abs(expectedWithoutNoFail - expectedWithNoFail) <= 0.0001d)
        {
            return false;
        }

        var actual = Math.Max(0d, (double)modifiedScore / multipliedScore);
        var distanceToWithoutNoFail = Math.Abs(actual - expectedWithoutNoFail);
        var distanceToWithNoFail = Math.Abs(actual - expectedWithNoFail);
        return distanceToWithNoFail + 0.02d < distanceToWithoutNoFail;
    }

    private static double ResolveBaseGameModifierMultiplier(IReadOnlyList<string> modifiers, bool includeNoFail)
    {
        var multiplier = 1d;
        foreach (var modifier in modifiers.Select(BeatLeaderModifierPolicy.NormalizeModifierToken).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            multiplier += modifier switch
            {
                "NF" when includeNoFail => -0.50d,
                "SS" => -0.30d,
                "NB" => -0.10d,
                "NO" => -0.05d,
                "NA" => -0.30d,
                "DA" => 0.07d,
                "FS" => 0.08d,
                "SF" => 0.10d,
                "GN" => 0.11d,
                _ => 0d
            };
        }

        return Math.Max(0d, multiplier);
    }

    private double GetSecondsUntilNextScorableNote(double songTime)
    {
        if (activeNoteTimes.Count == 0)
        {
            return double.PositiveInfinity;
        }

        for (var i = 0; i < activeNoteTimes.Count; i++)
        {
            var noteTime = activeNoteTimes[i];
            if (noteTime <= songTime)
            {
                continue;
            }

            return Math.Max(0d, noteTime - songTime);
        }

        return double.PositiveInfinity;
    }

    private BreakTimelineState TryResolveTimelineBreak(double songTime)
    {
        if (activeBreakWindows.Count == 0)
        {
            currentActiveBreakWindowIndex = -1;
            return BreakTimelineState.Collapsed;
        }

        for (var i = 0; i < activeBreakWindows.Count; i++)
        {
            var window = activeBreakWindows[i];
            if (songTime < window.StartTime)
            {
                currentActiveBreakWindowIndex = -1;
                return BreakTimelineState.Collapsed;
            }

            if (songTime >= window.StartTime && songTime < window.EndTime)
            {
                currentActiveBreakWindowIndex = i;
                return new BreakTimelineState(
                    songTime < Math.Max(window.StartTime, window.EndTime - CollapseBeforeNextNoteSeconds),
                    window.StartTime,
                    window.EndTime);
            }
        }

        currentActiveBreakWindowIndex = -1;
        return BreakTimelineState.Collapsed;
    }

    private bool IsAfterLastNote(double songTime)
    {
        if (activeNoteTimes.Count == 0)
        {
            return false;
        }

        return songTime >= activeNoteTimes[activeNoteTimes.Count - 1];
    }

    private double GetLastPlannedNoteTime()
    {
        if (activeNoteTimes.Count == 0)
        {
            return -1d;
        }

        return activeNoteTimes[activeNoteTimes.Count - 1];
    }

    private IReadOnlyList<BreakWindow> BuildBreakWindows(IReadOnlyList<double> noteTimes)
    {
        if (noteTimes.Count == 0)
        {
            return Array.Empty<BreakWindow>();
        }

        var minBreakLength = Math.Max(0d, config.ExpandOnBreakSeconds);
        var windows = new List<BreakWindow>();
        var firstNoteTime = noteTimes[0];
        if (firstNoteTime >= minBreakLength)
        {
            windows.Add(new BreakWindow(0d, firstNoteTime));
        }

        for (var i = 0; i < noteTimes.Count - 1; i++)
        {
            var start = noteTimes[i];
            var end = noteTimes[i + 1];
            if (end - start >= minBreakLength)
            {
                windows.Add(new BreakWindow(start, end));
            }
        }

        return windows;
    }

    private void ResetRunState()
    {
        sessionActive = false;
        pageFetchInFlight = false;
        sessionStartupInFlight = false;
        isPaused = false;
        lastOverlaySnapshotUtc = DateTimeOffset.MinValue;
        lastRuntimePageFetchUtc = DateTimeOffset.MinValue;
        activeModifiers = Array.Empty<string>();
        activeNoteTimes = Array.Empty<double>();
        activeBreakWindows = Array.Empty<BreakWindow>();
        activeSongLengthSeconds = null;
        activeBeatsPerMinute = null;
        hudSuppressedByGame = false;
        activeReplayMode = false;
        pendingReplayMode = false;
        levelFailedWithNoFail = false;
        RestoreReplayAlwaysExpandOverride();
        sessionManager.SetRuntimeAlwaysExpand(false);
        runtimeBeatmapTimelineLoadAttempted = false;
        currentActiveBreakWindowIndex = -1;
        lastKnownSongTime = 0d;
        lastBreakTimelineCheckUtc = DateTimeOffset.MinValue;
        lastTimelineBreakExpanded = false;
        ClearRuntimeReferences();
        overlayRenderer?.SetWorldVisible(false);
        overlayRenderer?.SetHudSuppressed(false);
        overlayRenderer?.SetGameplayActive(false);
        overlayRenderer?.SetReplayModeRenderingEnabled(false);
    }

    private OverlaySuppressionReason ResolveSuppressionReason(BeatmapSessionInfo beatmap)
    {
        if (!config.Enabled)
        {
            return OverlaySuppressionReason.ModDisabled;
        }

        if (beatmap.IsPracticeMode)
        {
            return OverlaySuppressionReason.Practice;
        }

        if (HasNoTextsAndHuds(activeModifiers))
        {
            return OverlaySuppressionReason.NoTextsAndHuds;
        }

        if (beatmap.HudSuppressedByGame)
        {
            return OverlaySuppressionReason.NoTextsAndHuds;
        }

        if (BeatLeaderModifierPolicy.IsZenMode(activeModifiers))
        {
            return OverlaySuppressionReason.ZenMode;
        }

        return OverlaySuppressionReason.None;
    }

    private static bool HasNoTextsAndHuds(IReadOnlyList<string>? modifiers)
    {
        return modifiers != null
            && modifiers.Any(mod =>
                string.Equals(mod, "NTH", StringComparison.OrdinalIgnoreCase)
                || string.Equals(mod, "NOHUD", StringComparison.OrdinalIgnoreCase)
                || string.Equals(mod, "NoTextsAndHUDs", StringComparison.OrdinalIgnoreCase));
    }

    private void SuppressOverlaySession(OverlaySuppressionReason reason)
    {
        sessionCancellation?.Cancel();
        sessionCancellation?.Dispose();
        sessionCancellation = null;
        sessionManager.End();
        RestoreReplayAlwaysExpandOverride();
        sessionManager.SetRuntimeAlwaysExpand(false);
        sessionActive = false;
        pageFetchInFlight = false;
        sessionStartupInFlight = false;
        lastDetectedBeatmap = null;
        lastRuntimePageFetchUtc = DateTimeOffset.MinValue;
        hudSuppressedByGame = false;
        pendingReplayMode = false;
        overlayRenderer?.SetWorldVisible(false);
        overlayRenderer?.SetGameplayActive(false);
        overlayRenderer?.SetHudSuppressed(false);
        overlayRenderer?.SetReplayModeRenderingEnabled(false);
        SetRendererViewModel(OverlayViewModel.Hidden());
        if (reason == OverlaySuppressionReason.ZenMode)
        {
            logger.Info("runtime_zen_mode_suppressed", "Zen Mode is enabled; BeatRelay session will not load.");
        }

        logger.Info("runtime_session_skipped", $"Overlay disabled for this session. reason={reason}");
    }

    private static bool IsReplaySession(BeatmapSessionInfo beatmap)
    {
        if (beatmap.IsReplayMode)
        {
            return true;
        }

        return beatmap.ReplayScoreId.GetValueOrDefault() > 0;
    }

    private static bool IsReplayRuntimeContext(object scoreController)
    {
        if (IsReplayLikeObject(scoreController))
        {
            return true;
        }

        var candidates = new[]
        {
            ReadMember(scoreController, "_gameplayCoreSceneSetupData", "gameplayCoreSceneSetupData"),
            ReadMember(scoreController, "_gameplayCoreSceneSetupData", "gameplayCoreSceneSetupData", "_levelScenesTransitionSetupData", "levelScenesTransitionSetupData"),
            ReadMember(scoreController, "_audioTimeSyncController", "audioTimeSyncController"),
            ReadMember(scoreController, "_beatmapObjectManager", "beatmapObjectManager"),
            ReadMember(scoreController, "_beatmapCallbacksController", "beatmapCallbacksController")
        };

        foreach (var candidate in candidates)
        {
            if (IsReplayLikeObject(candidate))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsReplayLikeObject(object? value)
    {
        if (value == null)
        {
            return false;
        }

        var type = value.GetType();
        var typeName = type.FullName ?? type.Name;
        if (typeName.IndexOf("Replay", StringComparison.OrdinalIgnoreCase) >= 0
            || typeName.IndexOf("BSOR", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return true;
        }

        return ReadBool(value, "isStartedAsReplay", "IsStartedAsReplay", "_isStartedAsReplay") == true
            || ReadMember(value, "replayData", "_replayData", "replay", "_replay", "replayMetaData", "_replayMetaData", "replayMetadata", "_replayMetadata", "lastPlayedReplay", "LastPlayedReplay", "mainReplay", "MainReplay") != null
            || ReadInt(value, "replayScoreId", "_replayScoreId", "scoreId", "_scoreId").GetValueOrDefault() > 0;
    }

    private static bool IsTimelineBreakExpanded(BeatmapRunState runState)
    {
        if (runState == null)
        {
            return false;
        }

        return runState.IsInPlannedBreak;
    }

    private void EnsureRuntimeTimelineLoaded(object scoreController)
    {
        runtimeBeatmapTimelineLoadAttempted = true;
        if (activeReplayMode)
        {
            return;
        }

        var beatmapData = ResolveRuntimeBeatmapData(scoreController);
        if (beatmapData == null)
        {
            logger.Info("runtime_note_timing_runtime_lookup_failed", "source=beatmap_data; reason=not_found");
            return;
        }

        var runtimeNoteTimes = NormalizeNoteTimes(
            ExtractRuntimeScorableNoteTimes(beatmapData),
            activeSongLengthSeconds,
            activeBeatsPerMinute);
        if (runtimeNoteTimes.Count == 0)
        {
            logger.Info("runtime_note_timing_runtime_lookup_failed", "source=beatmap_data; reason=no_scorable_notes");
            return;
        }

        activeNoteTimes = runtimeNoteTimes;
        activeBreakWindows = BuildBreakWindows(activeNoteTimes);
        currentActiveBreakWindowIndex = -1;
        logger.Info(
            "runtime_note_timing_loaded",
            $"source=beatmap_data; activeNoteTimes={activeNoteTimes.Count}; lastNoteTime={activeNoteTimes[activeNoteTimes.Count - 1]:0.000}; breakWindows={activeBreakWindows.Count}; minBreakSeconds={Math.Max(0d, config.ExpandOnBreakSeconds):0.###}");
    }

    private object? ResolveRuntimeBeatmapData(object scoreController)
    {
        var possible = new[]
        {
            ReadMember(scoreController, "_transformedBeatmapData", "transformedBeatmapData"),
            ReadMember(scoreController, "_beatmapData", "beatmapData"),
            ReadMember(ReadMember(scoreController, "_beatmapObjectManager"), "_beatmapData", "beatmapData", "_transformedBeatmapData", "transformedBeatmapData"),
            ReadMember(ReadMember(scoreController, "_gameplayCoreSceneSetupData"), "_transformedBeatmapData", "transformedBeatmapData", "_beatmapData", "beatmapData")
        };

        var directHit = possible.FirstOrDefault(item => item != null && ReadMember(item, "allBeatmapDataItems", "_allBeatmapDataItems") != null);
        if (directHit != null)
        {
            return directHit;
        }

        var setupData = ReadMember(scoreController, "_gameplayCoreSceneSetupData");
        return FindBeatmapDataObject(
            scoreController,
            ReadMember(scoreController, "_beatmapObjectManager"),
            setupData,
            ReadMember(setupData, "_transformedBeatmapData", "transformedBeatmapData"),
            ReadMember(setupData, "_beatmapData", "beatmapData"));
    }

    private static object? FindBeatmapDataObject(params object?[] roots)
    {
        foreach (var root in roots)
        {
            var found = FindBeatmapDataObject(root, new HashSet<object>(), depth: 0);
            if (found != null)
            {
                return found;
            }
        }

        return null;
    }

    private static object? FindBeatmapDataObject(object? source, ISet<object> visited, int depth)
    {
        if (source == null || depth > 6)
        {
            return null;
        }

        var sourceType = source.GetType();
        if (source is string || sourceType.IsPrimitive || !visited.Add(source))
        {
            return null;
        }

        if (ReadMember(source, "allBeatmapDataItems", "_allBeatmapDataItems") != null)
        {
            return source;
        }

        if (source is IEnumerable enumerable)
        {
            var inspected = 0;
            foreach (var item in enumerable)
            {
                var found = FindBeatmapDataObject(item, visited, depth + 1);
                if (found != null)
                {
                    return found;
                }

                inspected++;
                if (inspected >= 3000)
                {
                    break;
                }
            }

            return null;
        }

        const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        foreach (var property in sourceType.GetProperties(Flags))
        {
            if (property.GetIndexParameters().Length != 0 || !ShouldInspectBeatmapMember(property.Name))
            {
                continue;
            }

            try
            {
                var found = FindBeatmapDataObject(property.GetValue(source), visited, depth + 1);
                if (found != null)
                {
                    return found;
                }
            }
            catch
            {
            }
        }

        foreach (var field in sourceType.GetFields(Flags))
        {
            if (!ShouldInspectBeatmapMember(field.Name))
            {
                continue;
            }

            try
            {
                var found = FindBeatmapDataObject(field.GetValue(source), visited, depth + 1);
                if (found != null)
                {
                    return found;
                }
            }
            catch
            {
            }
        }

        return null;
    }

    private static bool ShouldInspectBeatmapMember(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        var lower = name.ToLowerInvariant();
        return lower.Contains("beatmap")
            || lower.Contains("note")
            || lower.Contains("object")
            || lower.Contains("item")
            || lower.Contains("data")
            || lower.Contains("transformed")
            || lower.Contains("setup");
    }

    private static IReadOnlyList<double> ExtractRuntimeScorableNoteTimes(object beatmapData)
    {
        var allItems = ReadMember(beatmapData, "allBeatmapDataItems", "_allBeatmapDataItems") as IEnumerable;
        if (allItems == null)
        {
            var recursiveResults = new List<double>();
            CollectRuntimeNoteTimes(beatmapData, recursiveResults, new HashSet<object>(), depth: 0);
            return recursiveResults
                .Where(time => time >= 0)
                .Distinct()
                .OrderBy(time => time)
                .ToList();
        }

        var results = new List<double>();
        foreach (var item in allItems)
        {
            if (item == null || !IsScorableNoteData(item))
            {
                continue;
            }

            var time = ReadFloat(item, "time", "_time", "beat", "_beat");
            if (time.HasValue && time.Value >= 0)
            {
                results.Add(time.Value);
            }
        }

        return results
            .Distinct()
            .OrderBy(time => time)
            .ToList();
    }

    private static void CollectRuntimeNoteTimes(object? source, ICollection<double> results, ISet<object> visited, int depth)
    {
        if (source == null || depth > 5 || results.Count > 25000)
        {
            return;
        }

        var sourceType = source.GetType();
        if (source is string || sourceType.IsPrimitive || !visited.Add(source))
        {
            return;
        }

        if (IsScorableNoteData(source))
        {
            var noteTime = ReadFloat(source, "time", "_time", "beat", "_beat");
            if (noteTime.HasValue && noteTime.Value >= 0)
            {
                results.Add(noteTime.Value);
            }

            return;
        }

        if (source is IEnumerable enumerable)
        {
            var inspected = 0;
            foreach (var item in enumerable)
            {
                CollectRuntimeNoteTimes(item, results, visited, depth + 1);
                inspected++;
                if (inspected >= 3000)
                {
                    break;
                }
            }

            return;
        }

        const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        foreach (var property in sourceType.GetProperties(Flags))
        {
            if (property.GetIndexParameters().Length != 0 || !ShouldInspectBeatmapMember(property.Name))
            {
                continue;
            }

            try
            {
                CollectRuntimeNoteTimes(property.GetValue(source), results, visited, depth + 1);
            }
            catch
            {
            }
        }

        foreach (var field in sourceType.GetFields(Flags))
        {
            if (!ShouldInspectBeatmapMember(field.Name))
            {
                continue;
            }

            try
            {
                CollectRuntimeNoteTimes(field.GetValue(source), results, visited, depth + 1);
            }
            catch
            {
            }
        }
    }

    private static bool IsScorableNoteData(object item)
    {
        var typeName = item.GetType().Name;
        var lowerTypeName = typeName.ToLowerInvariant();
        if (!lowerTypeName.Contains("note"))
        {
            return false;
        }

        if (lowerTypeName.Contains("bomb"))
        {
            return false;
        }

        if (lowerTypeName.Contains("obstacle") || lowerTypeName.Contains("wall"))
        {
            return false;
        }

        var colorType = ReadInt(item, "colorType", "_colorType", "type", "_type");
        if (colorType.HasValue && colorType.Value >= 0 && colorType.Value <= 1)
        {
            return true;
        }

        var cutDirection = ReadInt(item, "cutDirection", "_cutDirection");
        if (cutDirection.HasValue)
        {
            return true;
        }

        var lineIndex = ReadInt(item, "lineIndex", "_lineIndex", "line", "_line", "x", "_x");
        var lineLayer = ReadInt(item, "lineLayer", "_lineLayer", "y", "_y");
        if (lineIndex.HasValue && lineLayer.HasValue)
        {
            return true;
        }

        if (colorType.HasValue && (colorType.Value < 0 || colorType.Value > 1))
        {
            return false;
        }

        var scoringType = ReadInt(item, "scoringType", "_scoringType", "gameplayType", "_gameplayType");
        if (scoringType.HasValue && scoringType.Value != 0)
        {
            return false;
        }

        return true;
    }

    private static IReadOnlyList<double> NormalizeNoteTimes(IReadOnlyList<double> noteTimes, double? songLengthSeconds, double? beatsPerMinute)
    {
        var cleaned = (noteTimes ?? Array.Empty<double>())
            .Where(time => time >= 0)
            .Distinct()
            .OrderBy(time => time)
            .ToList();
        if (cleaned.Count == 0)
        {
            return cleaned;
        }

        if (!songLengthSeconds.HasValue || songLengthSeconds.Value <= 0 || !beatsPerMinute.HasValue || beatsPerMinute.Value <= 0)
        {
            return cleaned;
        }

        var lastTime = cleaned[cleaned.Count - 1];
        if (lastTime <= songLengthSeconds.Value * 1.15d)
        {
            return cleaned;
        }

        var secondsPerBeat = 60d / beatsPerMinute.Value;
        return cleaned
            .Select(time => time * secondsPerBeat)
            .Where(time => time >= 0)
            .OrderBy(time => time)
            .ToList();
    }

    private void EndActiveSession(string reason)
    {
        if (sessionCancellation != null)
        {
            sessionCancellation.Cancel();
            sessionCancellation.Dispose();
            sessionCancellation = null;
        }

        sessionManager.End();
        RestoreReplayAlwaysExpandOverride();
        sessionManager.SetRuntimeAlwaysExpand(false);
        ResetRunState();
        SetRendererViewModel(OverlayViewModel.Hidden());
        logger.Info("runtime_session_exit", $"Overlay session ended ({reason}).");
    }

    private void RefreshHudSuppression(object? scoreController)
    {
        if (scoreController != null)
        {
            EnsureRuntimeReferences(scoreController);
        }

        var suppressed = HasNoTextsAndHuds(activeModifiers)
            || IsNoTextsAndHudsSettingEnabled(cachedGameplayModifiers)
            || IsNoTextsAndHudsSettingEnabled(cachedPlayerSpecificSettings);

        if (hudSuppressedByGame == suppressed)
        {
            return;
        }

        hudSuppressedByGame = suppressed;
        overlayRenderer?.SetHudSuppressed(hudSuppressedByGame);
    }

    private void EnsureRuntimeReferences(object scoreController)
    {
        if (ReferenceEquals(cachedRuntimeScoreController, scoreController))
        {
            return;
        }

        cachedRuntimeScoreController = scoreController;
        cachedAudioTimeSyncController = ReadMember(scoreController, "_audioTimeSyncController", "audioTimeSyncController");
        cachedGameplayCoreSceneSetupData = ReadMember(scoreController, "_gameplayCoreSceneSetupData", "gameplayCoreSceneSetupData");
        cachedLevelScenesTransitionSetupData = ReadMember(cachedGameplayCoreSceneSetupData, "_levelScenesTransitionSetupData", "levelScenesTransitionSetupData");
        cachedStandardGameplaySceneSetupData = ReadMember(cachedLevelScenesTransitionSetupData, "_standardGameplaySceneSetupData", "standardGameplaySceneSetupData");
        cachedGameplayModifiers =
            ReadMember(scoreController, "_gameplayModifiers", "gameplayModifiers")
            ?? ReadMember(cachedGameplayCoreSceneSetupData, "_gameplayModifiers", "gameplayModifiers")
            ?? ReadMember(cachedLevelScenesTransitionSetupData, "_gameplayModifiers", "gameplayModifiers")
            ?? ReadMember(cachedStandardGameplaySceneSetupData, "_gameplayModifiers", "gameplayModifiers");
        cachedPlayerSpecificSettings =
            ReadMember(scoreController, "_playerSpecificSettings", "playerSpecificSettings")
            ?? ReadMember(cachedGameplayCoreSceneSetupData, "_playerSpecificSettings", "playerSpecificSettings")
            ?? ReadMember(cachedLevelScenesTransitionSetupData, "_playerSpecificSettings", "playerSpecificSettings")
            ?? ReadMember(cachedStandardGameplaySceneSetupData, "_playerSpecificSettings", "playerSpecificSettings");
    }

    private void ClearRuntimeReferences()
    {
        runtimeReplayContextChecksRemaining = 10;
        cachedRuntimeScoreController = null;
        cachedAudioTimeSyncController = null;
        cachedGameplayCoreSceneSetupData = null;
        cachedLevelScenesTransitionSetupData = null;
        cachedStandardGameplaySceneSetupData = null;
        cachedGameplayModifiers = null;
        cachedPlayerSpecificSettings = null;
    }

    internal static bool IsHudSuppressedByRuntimeSettings(object? scoreController)
    {
        if (scoreController == null)
        {
            return false;
        }

        var gameplayCoreSceneSetupData = ReadMember(scoreController, "_gameplayCoreSceneSetupData", "gameplayCoreSceneSetupData");
        var levelScenesTransitionSetupData = ReadMember(gameplayCoreSceneSetupData, "_levelScenesTransitionSetupData", "levelScenesTransitionSetupData");
        var standardGameplaySceneSetupData = ReadMember(levelScenesTransitionSetupData, "_standardGameplaySceneSetupData", "standardGameplaySceneSetupData");
        var gameplayModifiers =
            ReadMember(scoreController, "_gameplayModifiers", "gameplayModifiers")
            ?? ReadMember(gameplayCoreSceneSetupData, "_gameplayModifiers", "gameplayModifiers")
            ?? ReadMember(levelScenesTransitionSetupData, "_gameplayModifiers", "gameplayModifiers")
            ?? ReadMember(standardGameplaySceneSetupData, "_gameplayModifiers", "gameplayModifiers");

        if (IsNoTextsAndHudsSettingEnabled(gameplayModifiers))
        {
            return true;
        }

        var playerSpecificSettings =
            ReadMember(scoreController, "_playerSpecificSettings", "playerSpecificSettings")
            ?? ReadMember(gameplayCoreSceneSetupData, "_playerSpecificSettings", "playerSpecificSettings")
            ?? ReadMember(levelScenesTransitionSetupData, "_playerSpecificSettings", "playerSpecificSettings")
            ?? ReadMember(standardGameplaySceneSetupData, "_playerSpecificSettings", "playerSpecificSettings");

        return IsNoTextsAndHudsSettingEnabled(playerSpecificSettings);
    }

    private static bool IsNoTextsAndHudsSettingEnabled(object? value)
    {
        return ReadBool(
            value,
            "noTextsAndHuds",
            "_noTextsAndHuds",
            "noTextsAndHUDs",
            "_noTextsAndHUDs",
            "hideTextsAndHuds",
            "_hideTextsAndHuds",
            "hideTextsAndHUDs",
            "_hideTextsAndHUDs",
            "hideTextAndHuds",
            "_hideTextAndHuds",
            "hideTextAndHUDs",
            "_hideTextAndHUDs") == true;
    }

    private void OnActiveSceneChanged(Scene from, Scene to)
    {
        EnsureOverlayInitialized();
        if (!IsLikelyMenuScene(to.name))
        {
            SetCustomizationPreviewVisible(false);
        }

        if (IsLikelyMenuScene(to.name))
        {
            lastDetectedBeatmap = null;
        }

        if (!sessionActive && !isPaused)
        {
            return;
        }

        if (IsLikelyGameplayScene(to.name))
        {
            return;
        }

        EndActiveSession("scene:" + (string.IsNullOrWhiteSpace(to.name) ? "(unknown)" : to.name));
    }

    private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        EnsureOverlayInitialized();
        if (!IsLikelyGameplayScene(scene.name) && !customizationPreviewActive)
        {
            overlayRenderer?.SetWorldVisible(false);
            overlayRenderer?.SetGameplayActive(false);
            overlayRenderer?.SetCustomizationPreviewActive(false);
        }

        if (IsLikelyGameplayScene(scene.name) && (sessionActive || isPaused))
        {
            overlayRenderer?.Configure(config);
            overlayRenderer?.SetGameplayActive(true);
            overlayRenderer?.SetHudSuppressed(hudSuppressedByGame);
            overlayRenderer?.SetWorldVisible(config.Enabled && !hudSuppressedByGame);
            logger.Info("runtime_overlay_respawn_checked", $"Gameplay scene loaded; refreshed overlay renderer for {scene.name}.");
        }
    }

    private void SetRendererViewModel(OverlayViewModel? viewModel)
    {
        lastRendererViewModel = viewModel ?? OverlayViewModel.Hidden();
        EnsureOverlayInitialized();
        overlayRenderer?.SetViewModel(lastRendererViewModel);
    }

    private void EnsureOverlayInitialized()
    {
        var recreated = false;
        var recreateReason = string.Empty;

        if (overlayRenderer == null)
        {
            overlayRenderer = InGameOverlayRenderer.Create(config, logger);
            recreated = true;
            recreateReason = "missing_renderer";
        }
        else
        {
            try
            {
                if (overlayRenderer.gameObject == null)
                {
                    overlayRenderer = InGameOverlayRenderer.Create(config, logger);
                    recreated = true;
                    recreateReason = "missing_game_object";
                }
            }
            catch (MissingReferenceException)
            {
                overlayRenderer = InGameOverlayRenderer.Create(config, logger);
                recreated = true;
                recreateReason = "destroyed_renderer";
            }
        }

        if (overlayRenderer == null)
        {
            return;
        }

        var sceneName = SceneManager.GetActiveScene().name;
        var gameplayScene = IsLikelyGameplayScene(sceneName);
        var menuScene = IsLikelyMenuScene(sceneName);

        if (recreated)
        {
            overlayRenderer.Configure(config);
            overlayRenderer.SetReplayModeRenderingEnabled(activeReplayMode);
            overlayRenderer.SetCustomizationPreviewActive(customizationPreviewActive && menuScene);
            overlayRenderer.SetHudSuppressed(hudSuppressedByGame);
            overlayRenderer.SetViewModel(lastRendererViewModel);
            overlayRenderer.SetGameplayActive((gameplayScene && (sessionActive || isPaused)) || (menuScene && customizationPreviewActive));
            overlayRenderer.SetWorldVisible(ResolveCurrentWorldVisibility(gameplayScene, menuScene));
            logger.Warn("runtime_overlay_renderer_recreated", $"reason={recreateReason}; scene={sceneName}; sessionActive={sessionActive}; previewActive={customizationPreviewActive}");
        }

        if (!overlayRenderer.gameObject.activeSelf && sessionActive)
        {
            overlayRenderer.SetGameplayActive(gameplayScene);
            overlayRenderer.SetWorldVisible(ResolveCurrentWorldVisibility(gameplayScene, menuScene));
        }
    }

    private bool ResolveCurrentWorldVisibility(bool gameplayScene, bool menuScene)
    {
        if (customizationPreviewActive && menuScene)
        {
            return !customizationPreviewWorldSuppressed;
        }

        return gameplayScene && config.Enabled && !hudSuppressedByGame && (sessionActive || isPaused);
    }

    private static BeatmapSessionInfo CloneBeatmapSessionInfo(BeatmapSessionInfo source)
    {
        return new BeatmapSessionInfo
        {
            Hash = source.Hash,
            Difficulty = source.Difficulty,
            Mode = source.Mode,
            SongName = source.SongName,
            MapperName = source.MapperName,
            ActiveModifiers = source.ActiveModifiers.ToList(),
            NoteTimes = source.NoteTimes.ToList(),
            RuntimeBeatmapData = source.RuntimeBeatmapData,
            SongLengthSeconds = source.SongLengthSeconds,
            BeatsPerMinute = source.BeatsPerMinute,
            IsReplayMode = source.IsReplayMode,
            IsPracticeMode = source.IsPracticeMode,
            HudSuppressedByGame = source.HudSuppressedByGame,
            ReplayScoreId = source.ReplayScoreId
        };
    }

    private static bool IsLikelyGameplayScene(string sceneName)
    {
        if (string.IsNullOrWhiteSpace(sceneName))
        {
            return false;
        }

        var name = sceneName.Trim();
        return name.IndexOf("GameCore", StringComparison.OrdinalIgnoreCase) >= 0
            || name.IndexOf("Gameplay", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static bool IsLikelyMenuScene(string sceneName)
    {
        return !string.IsNullOrWhiteSpace(sceneName)
            && sceneName.IndexOf("Menu", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static IReadOnlyList<string> ParseActiveModifiers(IReadOnlyList<string>? source)
    {
        if (source == null || source.Count == 0)
        {
            return Array.Empty<string>();
        }

        var values = source
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(BeatLeaderModifierPolicy.NormalizeModifierToken)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return values.Count == 0 ? Array.Empty<string>() : values;
    }

    private static string FormatViewModel(OverlayViewModel viewModel)
    {
        return $"mode={viewModel.Mode}; rank={ValueOrEmpty(viewModel.RankText)}; message={ValueOrEmpty(viewModel.MessageText)}; map={ValueOrEmpty(viewModel.MapContextText)}; rows={viewModel.Rows.Count}";
    }

    private static string ValueOrEmpty(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? "(empty)" : value;
    }

    private static int? ReadInt(object? target, params string[] names)
    {
        var value = ReadMember(target, names);
        if (value == null)
        {
            return null;
        }

        return Convert.ToInt32(value);
    }

    private static float? ReadFloat(object? target, params string[] names)
    {
        var value = ReadMember(target, names);
        if (value == null)
        {
            return null;
        }

        return Convert.ToSingle(value);
    }

    private static bool? ReadBool(object? target, params string[] names)
    {
        var value = ReadMember(target, names);
        if (value == null)
        {
            return null;
        }

        return Convert.ToBoolean(value);
    }

    private static object? ReadMember(object? target, params string[] names)
    {
        if (target == null)
        {
            return null;
        }

        var type = target.GetType();
        foreach (var name in names)
        {
            var reader = MemberReaders.GetOrAdd(
                new MemberLookupKey(type, name),
                static key => CreateMemberReader(key.Type, key.Name));
            if (!ReferenceEquals(reader, MissingMemberReader))
            {
                return reader(target);
            }
        }

        return null;
    }

    private static Func<object, object?> CreateMemberReader(Type type, string name)
    {
        const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        var property = type.GetProperty(name, Flags);
        if (property != null)
        {
            return target => property.GetValue(target);
        }

        var field = type.GetField(name, Flags);
        if (field != null)
        {
            return target => field.GetValue(target);
        }

        return MissingMemberReader;
    }

    private static double ElapsedMilliseconds(long startedTimestamp)
    {
        return (System.Diagnostics.Stopwatch.GetTimestamp() - startedTimestamp)
            * 1000d
            / System.Diagnostics.Stopwatch.Frequency;
    }

    private static string? ReadStringDeep(object? target, int maxDepth, ISet<object> visited, params string[] names)
    {
        if (target == null || maxDepth < 0)
        {
            return null;
        }

        var targetType = target.GetType();
        if (target is string text)
        {
            return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        }

        if (targetType.IsPrimitive || !visited.Add(target))
        {
            return null;
        }

        foreach (var name in names)
        {
            var value = ReadMember(target, name);
            var valueText = value?.ToString();
            if (!string.IsNullOrWhiteSpace(valueText))
            {
                return valueText.Trim();
            }
        }

        const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        foreach (var property in targetType.GetProperties(Flags))
        {
            if (property.GetIndexParameters().Length != 0 || !ShouldInspectIdentityMember(property.Name))
            {
                continue;
            }

            try
            {
                var result = ReadStringDeep(property.GetValue(target), maxDepth - 1, visited, names);
                if (!string.IsNullOrWhiteSpace(result))
                {
                    return result;
                }
            }
            catch
            {
            }
        }

        foreach (var field in targetType.GetFields(Flags))
        {
            if (!ShouldInspectIdentityMember(field.Name))
            {
                continue;
            }

            try
            {
                var result = ReadStringDeep(field.GetValue(target), maxDepth - 1, visited, names);
                if (!string.IsNullOrWhiteSpace(result))
                {
                    return result;
                }
            }
            catch
            {
            }
        }

        return null;
    }

    private static bool ShouldInspectIdentityMember(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        var lower = name.ToLowerInvariant();
        return lower.Contains("user")
            || lower.Contains("player")
            || lower.Contains("platform")
            || lower.Contains("id")
            || lower.Contains("profile");
    }

    private readonly struct MemberLookupKey : IEquatable<MemberLookupKey>
    {
        public MemberLookupKey(Type type, string name)
        {
            Type = type;
            Name = name;
        }

        public Type Type { get; }

        public string Name { get; }

        public bool Equals(MemberLookupKey other)
        {
            return ReferenceEquals(Type, other.Type)
                && string.Equals(Name, other.Name, StringComparison.Ordinal);
        }

        public override bool Equals(object? obj)
        {
            return obj is MemberLookupKey other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                return ((Type.GetHashCode() * 397) ^ StringComparer.Ordinal.GetHashCode(Name));
            }
        }
    }

    private readonly struct BreakWindow
    {
        public BreakWindow(double startTime, double endTime)
        {
            StartTime = startTime;
            EndTime = endTime;
        }

        public double StartTime { get; }

        public double EndTime { get; }
    }

    private readonly struct BreakTimelineState
    {
        public BreakTimelineState(bool isExpanded, double startTime, double endTime)
        {
            IsExpanded = isExpanded;
            StartTime = startTime;
            EndTime = endTime;
        }

        public static BreakTimelineState Collapsed { get; } = new(false, 0d, 0d);

        public bool IsExpanded { get; }

        public double StartTime { get; }

        public double EndTime { get; }
    }

    private enum OverlaySuppressionReason
    {
        None,
        ModDisabled,
        Replay,
        Practice,
        NoTextsAndHuds,
        ZenMode
    }
}
#endif
