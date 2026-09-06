#if NETFRAMEWORK
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Text;
using BeatRelay.BeatLeader;
using BeatRelay.Config;
using BeatRelay.Diagnostics;
using BeatRelay.UI;
using TMPro;
using UnityEngine;

namespace BeatRelay.BeatSaber;

public sealed class InGameOverlayRenderer : MonoBehaviour
{
    private const int MaxWorldLines = 11;
    private const float WorldLineSpacing = 0.095f;
    private const float WorldBodyFontSize = 0.39f;
    private const float WorldRankFontSize = 0.76f;
    private const float WorldRowRankX = 0.02f;
    private const float WorldRowNameX = 0.17f;
    private const float WorldRowTextYOffset = -0.012f;
    private const string ScoreSaberDesktopDetailSeparator = "\u00A0\u00A0";
    private const float WorldRowCompactValueX = 0.37f;
    private const float WorldRowExpandedValueX = 0.72f;
    private const float WorldRowAnimationOffsetScale = 0.0025f;
    private const float WorldBackgroundZ = 0.000f;
    private const float WorldHighlightZ = -0.002f;
    private const float WorldTextZ = -0.006f;
    private const int WorldTextSortingOrder = 100;
    private const float NoFailHighlightWaveSeconds = 0.9f;
    private const float DesktopPanelBaseWidth = 430f;
    private const string DefaultWorldPositionPreset = "AboveMultiplier";
    private static readonly Color WorldBackgroundTextureColor = new(0.02f, 0.025f, 0.035f, 1f);
    private static readonly Color WorldLocalHighlightColor = new(0.431f, 0.212f, 0.514f, 0.95f);
    private static readonly Color WorldNoFailHighlightColor = new(0.72f, 0.20f, 0.24f, 0.95f);
    private readonly object sync = new();

    private OverlayConfig config = new();
    private OverlayViewModel viewModel = OverlayViewModel.Hidden();
    private bool showInWorld;
    private bool gameplayActive;
    private bool hudSuppressed;
    private bool customizationPreviewActive;
    private bool replayModeRenderingEnabled;
    private double animatedRankValue;
    private int targetRankValue;
    private bool hasAnimatedRankValue;
    private float rankSlideOffsetY;
    private GameObject? worldRoot;
    private TMP_Text[]? worldLines;
    private TMP_Text[]? worldNameLines;
    private TMP_Text[]? worldValueLines;
    private GameObject? worldBackgroundObject;
    private GameObject? worldHighlightObject;
    private GameObject[]? worldRowContainers;
    private Texture2D? worldPanelBackgroundTexture;
    private Texture2D? worldHighlightTexture;
    private Texture2D? worldNoFailHighlightTexture;
    private bool forceWorldTextRefresh = true;
    private GUIStyle? panelStyle;
    private GUIStyle? titleStyle;
    private GUIStyle? rankStyle;
    private GUIStyle? bodyStyle;
    private GUIStyle? rowStyle;
    private GUIStyle? localRowStyle;
    private GUIStyle? rowValueStyle;
    private Texture2D? localRowBackgroundTexture;
    private Texture2D? desktopPanelBackgroundTexture;
    private readonly Dictionary<string, float> rowAnimationOffsets = new(StringComparer.Ordinal);
    private bool checkedPreferredFontAvailability;
    private bool preferredFontAvailable;
    private float? lastConfiguredScale;
    private float? lastConfiguredDesktopBackgroundOpacity;
    private float rankFlashStrength;
    private Color rankFlashColor = Color.white;
    private readonly OverlayExpansionTransition expansionTransition = new();
    private readonly Dictionary<string, float> worldTextWidths = new(StringComparer.Ordinal);
    private readonly Dictionary<TMP_Text, WorldValueReveal> worldValueReveals = new();
    private float expansionBlend;
    private float targetExpansionBlend;
    private TMP_FontAsset? worldLinkedFont;
    private Material? worldLinkedFontMaterial;
    private bool worldTextPrewarmed;
    private IOverlayLogger? logger;
    private bool worldFontSelectionLogged;
    private bool desktopFontSelectionLogged;
    private bool desktopOverlayWasRendering;
    private string lastViewModelSignature = string.Empty;
    private float? noFailHighlightWaveStartTime;
    private bool countersPlusPositionLogged;
    private Transform? cachedCountersPlusBaseHud;
    private float cachedCountersPlusHudWidth = 3.2f;
    private float cachedCountersPlusHudHeight;
    private float cachedCountersPlusHudDepth = 7f;
    private float nextCountersPlusHudScanTime;
    private float nextWorldOverlayRecoveryLogTime;
    private long rendererRefreshCount;
    private double rendererRefreshTotalMilliseconds;
    private double rendererRefreshMaxMilliseconds;
    private float nextRendererPerformanceLogTime;
    private const float CountersPlusHudScanIntervalSeconds = 1.5f;

    public static InGameOverlayRenderer Create(OverlayConfig config, IOverlayLogger? logger = null)
    {
        var gameObject = new GameObject("BeatRelayRenderer");
        UnityEngine.Object.DontDestroyOnLoad(gameObject);
        var renderer = gameObject.AddComponent<InGameOverlayRenderer>();
        renderer.logger = logger;
        renderer.Configure(config);
        return renderer;
    }

    public void Configure(OverlayConfig overlayConfig)
    {
        if (overlayConfig == null)
        {
            throw new ArgumentNullException(nameof(overlayConfig));
        }

        lock (sync)
        {
            config = overlayConfig;
            forceWorldTextRefresh = true;
            lastViewModelSignature = string.Empty;
        }

        RefreshBehaviourEnabled();
    }
    public void SetCustomizationPreviewActive(bool active)
    {
        lock (sync)
        {
            if (customizationPreviewActive != active)
            {
                forceWorldTextRefresh = true;
            }

            customizationPreviewActive = active;
        }

        RefreshBehaviourEnabled();
    }

    public void SetViewModel(OverlayViewModel nextViewModel)
    {
        var normalizedViewModel = nextViewModel ?? OverlayViewModel.Hidden();
        lock (sync)
        {
            var previousRows = viewModel.Rows;
            var previousMode = viewModel.Mode;
            var wasNoFailPenaltyActive = viewModel.NoFailPenaltyActive;

            if (replayModeRenderingEnabled && normalizedViewModel.Mode != OverlayMode.Hidden)
            {
                normalizedViewModel.Mode = OverlayMode.Expanded;
            }

            var nextSignature = BuildViewModelSignature(normalizedViewModel, config);
            if (!customizationPreviewActive && string.Equals(nextSignature, lastViewModelSignature, StringComparison.Ordinal))
            {
                return;
            }

            viewModel = normalizedViewModel;
            lastViewModelSignature = nextSignature;
            forceWorldTextRefresh = true;
            if (!wasNoFailPenaltyActive && viewModel.NoFailPenaltyActive)
            {
                noFailHighlightWaveStartTime = Time.time;
            }

            targetExpansionBlend = viewModel.Mode == OverlayMode.Expanded ? 1f : 0f;
            if (previousMode != viewModel.Mode)
            {
                forceWorldTextRefresh = true;
            }

            if (replayModeRenderingEnabled && targetExpansionBlend > 0f)
            {
                expansionTransition.Reset(1d);
                expansionBlend = 1f;
            }

            if (!customizationPreviewActive && TryResolveLiveRank(viewModel, out var parsedRank))
            {
                if (!hasAnimatedRankValue)
                {
                    animatedRankValue = parsedRank;
                    targetRankValue = parsedRank;
                    hasAnimatedRankValue = true;
                }
                else if (targetRankValue != parsedRank)
                {
                    var rankDelta = parsedRank - targetRankValue;
                    targetRankValue = parsedRank;
                    rankFlashStrength = 1f;
                    rankFlashColor = rankDelta < 0 ? new Color(0.55f, 1f, 0.75f, 1f) : new Color(1f, 0.62f, 0.38f, 1f);
                    rankSlideOffsetY = rankDelta < 0 ? -18f : 18f;
                }
            }
        }

    }

    public void SetPaused(bool paused)
    {
        if (!paused)
        {
            return;
        }

        OverlayViewModel currentViewModel;
        lock (sync)
        {
            if (viewModel.Mode != OverlayMode.Hidden)
            {
                viewModel.Mode = OverlayMode.Expanded;
                targetExpansionBlend = 1f;
            }

            currentViewModel = viewModel;
        }

    }

    public void SetWorldVisible(bool visible)
    {
        var changed = false;
        lock (sync)
        {
            changed = showInWorld != visible;
            showInWorld = visible;
        }

        if (changed && !visible)
        {
            HideWorldOverlay();
        }

        RefreshBehaviourEnabled();
    }

    public void SetGameplayActive(bool active)
    {
        var shouldHide = false;
        lock (sync)
        {
            var wasActive = gameplayActive;
            gameplayActive = active;
            if (active)
            {
                if (!wasActive)
                {
                    worldTextPrewarmed = false;
                }
            }
            if (!active)
            {
                showInWorld = false;
                worldTextPrewarmed = false;
                shouldHide = !customizationPreviewActive;
            }
        }

        if (shouldHide)
        {
            HideWorldOverlay();
        }

        RefreshBehaviourEnabled();
    }

    public void SetHudSuppressed(bool suppressed)
    {
        var changed = false;
        lock (sync)
        {
            changed = hudSuppressed != suppressed;
            hudSuppressed = suppressed;
        }

        if (changed && suppressed)
        {
            HideWorldOverlay();
        }

        RefreshBehaviourEnabled();
    }

    public void SetReplayModeRenderingEnabled(bool enabled)
    {
        lock (sync)
        {
            replayModeRenderingEnabled = enabled;
            if (enabled && viewModel.Mode != OverlayMode.Hidden)
            {
                viewModel.Mode = OverlayMode.Expanded;
                expansionTransition.Reset(1d);
                expansionBlend = 1f;
                targetExpansionBlend = 1f;
            }
        }
    }

    public void Clear()
    {
        lock (sync)
        {
            viewModel = OverlayViewModel.Hidden();
            lastViewModelSignature = string.Empty;
            showInWorld = false;
            gameplayActive = false;
            rowAnimationOffsets.Clear();
            hasAnimatedRankValue = false;
            rankSlideOffsetY = 0f;
            panelStyle = null;
            titleStyle = null;
            rankStyle = null;
            bodyStyle = null;
            rowStyle = null;
            localRowStyle = null;
            rowValueStyle = null;
            DesktopTekoTextRenderer.Clear();
            DestroyTexture(ref localRowBackgroundTexture);
            DestroyTexture(ref desktopPanelBackgroundTexture);
            if (worldNoFailHighlightTexture != null)
            {
                UnityEngine.Object.Destroy(worldNoFailHighlightTexture);
                worldNoFailHighlightTexture = null;
            }

            lastConfiguredScale = null;
            lastConfiguredDesktopBackgroundOpacity = null;
            rankFlashStrength = 0f;
            rankFlashColor = Color.white;
            expansionTransition.Reset(0d);
            expansionBlend = 0f;
            targetExpansionBlend = 0f;
            DestroyWorldFontMaterials();
            worldLinkedFont = null;
            worldFontSelectionLogged = false;
            worldTextPrewarmed = false;
            noFailHighlightWaveStartTime = null;
            replayModeRenderingEnabled = false;
            cachedCountersPlusBaseHud = null;
            cachedCountersPlusHudWidth = 3.2f;
            cachedCountersPlusHudHeight = 0f;
            cachedCountersPlusHudDepth = 7f;
            nextCountersPlusHudScanTime = 0f;
            HideWorldOverlay();
        }

        RefreshBehaviourEnabled();
    }

    private void OnDestroy()
    {
        DesktopTekoTextRenderer.Clear();
        DestroyTexture(ref localRowBackgroundTexture);
        DestroyTexture(ref desktopPanelBackgroundTexture);
        DestroyTexture(ref worldPanelBackgroundTexture);
        DestroyTexture(ref worldHighlightTexture);
        DestroyTexture(ref worldNoFailHighlightTexture);
        DestroyWorldFontMaterials();
    }

    private static void DestroyTexture(ref Texture2D? texture)
    {
        if (texture != null)
        {
            UnityEngine.Object.Destroy(texture);
            texture = null;
        }
    }

    private void LateUpdate()
    {
        try
        {
            UpdateWorldOverlay();
        }
        catch (MissingReferenceException ex)
        {
            RecoverWorldOverlay("missing_reference", ex.Message);
        }
        catch (NullReferenceException ex)
        {
            RecoverWorldOverlay("null_reference", ex.Message);
        }
        catch (Exception ex)
        {
            RecoverWorldOverlay("unexpected_exception", ex.Message);
        }
    }

    private void RefreshBehaviourEnabled()
    {
        bool shouldBeEnabled;
        lock (sync)
        {
            shouldBeEnabled = config.Enabled
                && (customizationPreviewActive || (gameplayActive && !hudSuppressed));
            if (shouldBeEnabled && !enabled)
            {
                forceWorldTextRefresh = true;
            }
        }

        if (enabled == shouldBeEnabled)
        {
            return;
        }

        if (!shouldBeEnabled)
        {
            HideWorldOverlay();
            if (desktopOverlayWasRendering)
            {
                desktopOverlayWasRendering = false;
                DesktopTekoTextRenderer.Clear();
            }
        }

        enabled = shouldBeEnabled;
    }

    private void UpdateWorldOverlay()
    {
        OverlayConfig currentConfig;
        OverlayViewModel currentViewModel;
        bool currentShowInWorld;
        bool currentGameplayActive;
        bool currentHudSuppressed;
        bool currentCustomizationPreviewActive;
        bool currentForceWorldTextRefresh;
        lock (sync)
        {
            currentConfig = config;
            currentViewModel = viewModel;
            currentShowInWorld = showInWorld;
            currentGameplayActive = gameplayActive;
            currentHudSuppressed = hudSuppressed;
            currentCustomizationPreviewActive = customizationPreviewActive;
            currentForceWorldTextRefresh = forceWorldTextRefresh;
        }

        if (!currentCustomizationPreviewActive && !currentGameplayActive)
        {
            return;
        }

        var previousExpansionBlend = expansionBlend;
        UpdateRankAnimation();
        currentForceWorldTextRefresh |= previousExpansionBlend != expansionBlend;
        if (currentCustomizationPreviewActive)
        {
            EnsureWorldOverlay();
            if (!IsWorldOverlayUsable())
            {
                ResetWorldOverlay();
                EnsureWorldOverlay();
            }

            if (!IsWorldOverlayUsable())
            {
                return;
            }

            var shouldShowPreview = currentConfig.Enabled && currentShowInWorld && currentViewModel.Mode != OverlayMode.Hidden;
            SetActiveIfChanged(worldRoot, shouldShowPreview);
            if (!shouldShowPreview)
            {
                return;
            }

            PositionWorldPanelForCustomizationPreview(currentConfig);
            if (!worldTextPrewarmed)
            {
                PrewarmWorldTextMeshes();
                worldTextPrewarmed = true;
            }

            var previewAnimationActive = HasWorldAnimationInProgress();
            if (currentForceWorldTextRefresh || previewAnimationActive)
            {
                var renderStarted = currentConfig.DebugLogging
                    ? System.Diagnostics.Stopwatch.GetTimestamp()
                    : 0L;
                ApplyWorldText(currentConfig, currentViewModel);
                if (currentConfig.DebugLogging)
                {
                    RecordRendererPerformance(renderStarted, "customizer");
                }
                lock (sync)
                {
                    forceWorldTextRefresh = false;
                }
            }

            return;
        }

        EnsureWorldOverlay();
        if (!IsWorldOverlayUsable())
        {
            ResetWorldOverlay();
            EnsureWorldOverlay();
        }

        if (!IsWorldOverlayUsable())
        {
            return;
        }

        var shouldShow = currentGameplayActive && !currentHudSuppressed && currentShowInWorld && currentConfig.Enabled && currentViewModel.Mode != OverlayMode.Hidden;
        SetActiveIfChanged(worldRoot, shouldShow);
        if (!shouldShow)
        {
            return;
        }

        PositionWorldPanel(currentConfig);
        if (!worldTextPrewarmed)
        {
            PrewarmWorldTextMeshes();
            worldTextPrewarmed = true;
        }
        var animationActive = HasWorldAnimationInProgress();
        if (currentForceWorldTextRefresh || animationActive)
        {
            var renderStarted = currentConfig.DebugLogging
                ? System.Diagnostics.Stopwatch.GetTimestamp()
                : 0L;
            ApplyWorldText(currentConfig, currentViewModel);
            if (currentConfig.DebugLogging)
            {
                RecordRendererPerformance(renderStarted, "gameplay");
            }
            lock (sync)
            {
                forceWorldTextRefresh = false;
            }
        }
    }

    private void RecordRendererPerformance(long startedTimestamp, string mode)
    {
        var elapsedMilliseconds = (System.Diagnostics.Stopwatch.GetTimestamp() - startedTimestamp)
            * 1000d
            / System.Diagnostics.Stopwatch.Frequency;
        rendererRefreshCount++;
        rendererRefreshTotalMilliseconds += elapsedMilliseconds;
        rendererRefreshMaxMilliseconds = Math.Max(rendererRefreshMaxMilliseconds, elapsedMilliseconds);

        if (!config.DebugLogging || Time.unscaledTime < nextRendererPerformanceLogTime)
        {
            return;
        }

        nextRendererPerformanceLogTime = Time.unscaledTime + 30f;
        var averageMilliseconds = rendererRefreshCount <= 0
            ? 0d
            : rendererRefreshTotalMilliseconds / rendererRefreshCount;
        logger?.Info(
            "performance_renderer_summary",
            $"mode={mode}; refreshes={rendererRefreshCount}; averageMs={averageMilliseconds:0.###}; maxMs={rendererRefreshMaxMilliseconds:0.###}");
        rendererRefreshCount = 0;
        rendererRefreshTotalMilliseconds = 0d;
        rendererRefreshMaxMilliseconds = 0d;
    }

    private void RecoverWorldOverlay(string reason, string detail)
    {
        ResetWorldOverlay();
        lock (sync)
        {
            forceWorldTextRefresh = true;
            worldTextPrewarmed = false;
        }

        if (Time.unscaledTime >= nextWorldOverlayRecoveryLogTime)
        {
            nextWorldOverlayRecoveryLogTime = Time.unscaledTime + 2f;
            logger?.Warn("runtime_world_overlay_recovered", $"reason={reason}; detail={detail}");
        }
    }

    private void OnGUI()
    {
        if (Event.current != null && Event.current.type != EventType.Repaint)
        {
            return;
        }

        var previousMatrix = GUI.matrix;
        GUI.matrix = Matrix4x4.identity;

        OverlayConfig currentConfig;
        OverlayViewModel currentViewModel;
        bool currentGameplayActive;
        bool currentHudSuppressed;
        bool currentCustomizationPreviewActive;
        lock (sync)
        {
            currentConfig = config;
            currentViewModel = viewModel;
            currentGameplayActive = gameplayActive;
            currentHudSuppressed = hudSuppressed;
            currentCustomizationPreviewActive = customizationPreviewActive;
        }

        var shouldRenderDesktop = currentGameplayActive
            && !currentHudSuppressed
            && currentConfig.Enabled
            && currentConfig.EnableDesktopOverlay
            && currentViewModel.Mode != OverlayMode.Hidden;
        if (!shouldRenderDesktop)
        {
            if (desktopOverlayWasRendering)
            {
                desktopOverlayWasRendering = false;
                DesktopTekoTextRenderer.Clear();
            }

            GUI.matrix = previousMatrix;
            return;
        }

        desktopOverlayWasRendering = true;
        var scaled = (float)Math.Max(0.5, currentConfig.DesktopOverlayScale);
        EnsureStyles(scaled, GetBackgroundOpacity01(currentConfig, expanded: true));
        GUI.depth = -1000;
        GUI.color = Color.white;
        GUI.contentColor = Color.white;
        GUI.backgroundColor = Color.white;

        var useExpandedSettings = true;
        var rect = GetPanelRect("UpperRight", scaled, currentViewModel, useExpandedSettings);
        GUI.Box(rect, GUIContent.none, panelStyle);

        var padding = 14f * scaled;
        var y = rect.y + padding;
        var showBigRank = currentConfig.GetShowBigRank(useExpandedSettings);
        var bigRankScale = (float)currentConfig.GetBigRankScale(useExpandedSettings);
        var rowScale = (float)currentConfig.GetEffectivePlayerRowScale(useExpandedSettings);
        var showNames = currentConfig.GetShowNames(useExpandedSettings);
        var rankHeight = showBigRank ? Mathf.Max(72f * scaled, 72f * scaled * bigRankScale) : 0f;
        var rankGap = showBigRank ? 8f * scaled : 0f;
        var rowHeight = 28f * scaled * rowScale;
        var valueColumnInset = 18f * scaled;
        var scoreSaberMode = IsScoreSaberViewModel(currentViewModel);
        var rowGap = 1f * scaled;

        if (showBigRank)
        {
            rankStyle!.fontSize = Mathf.RoundToInt(Mathf.Min(112f, 46f * scaled * bigRankScale));
            rankStyle.normal.textColor = Color.white;
            DrawDesktopText(
                new Rect(rect.x + padding, y + (rankSlideOffsetY * scaled), rect.width - (padding * 2f), rankHeight + (18f * scaled)),
                GetDisplayedRankText(currentViewModel),
                rankStyle);
            y += rankHeight + rankGap;
        }

        foreach (var row in currentViewModel.Rows)
        {
            var isExpandedMode = true;
            var showExpandedDetails = isExpandedMode;
            var showPlayerCollapsedValueOnly = !isExpandedMode && row.IsLocalPlayer;
            var rowOffset = 0f;
            var rowX = rect.x + padding;
            var rowY = y + rowOffset;
            var resolvedStyle = row.IsLocalPlayer ? localRowStyle : rowStyle;
            if (!scoreSaberMode && row.IsLocalPlayer && localRowBackgroundTexture != null && currentConfig.GetShowHighlight(useExpandedSettings))
            {
                GUI.DrawTexture(
                    new Rect(rect.x + (8f * scaled), rowY + (3f * scaled), rect.width - (16f * scaled), rowHeight - (6f * scaled)),
                    localRowBackgroundTexture,
                    ScaleMode.StretchToFill);
            }

            var rankPrefix = row.Rank.ToString();
            var rankBlockWidth = Math.Max(32f * scaled, MeasureWidth(localRowStyle ?? rowStyle!, rankPrefix) + (8f * scaled));
            var rankStyleForRow = resolvedStyle;
            if (scoreSaberMode && row.IsLocalPlayer && resolvedStyle != null)
            {
                rankStyleForRow = new GUIStyle(resolvedStyle)
                {
                    normal = { textColor = new Color32(0x00, 0xC8, 0xFF, 0xFF) }
                };
            }

            DrawDesktopText(
                new Rect(rowX + (1f * scaled), rowY, rankBlockWidth, rowHeight),
                rankPrefix,
                rankStyleForRow);
            rowX += rankBlockWidth + (2f * scaled);

            var rowName = string.IsNullOrWhiteSpace(row.PlayerName) ? (row.IsLocalPlayer ? currentViewModel.DisplayName : "Unknown Player") : row.PlayerName;
            rowName = ClipOverlayPlayerName(rowName);

            if (scoreSaberMode)
            {
                var scoreText = FormatScoreSaberScoreText(currentConfig, currentViewModel, row, useExpandedSettings);
                var scoreWidth = string.IsNullOrWhiteSpace(scoreText)
                    ? 0f
                    : Math.Max(112f * scaled, MeasureWidth(rowValueStyle!, scoreText) + (12f * scaled));
                var scoreRectX = rect.x + rect.width - valueColumnInset - scoreWidth;
                var detailRect = new Rect(rowX + (2f * scaled), rowY, Math.Max(80f * scaled, scoreRectX - rowX - (32f * scaled)), rowHeight);
                var detailText = BuildScoreSaberDesktopDetailText(currentConfig, currentViewModel, row, useExpandedSettings, showNames ? rowName : string.Empty);
                if (!string.IsNullOrWhiteSpace(detailText))
                {
                    var detailStyle = new GUIStyle(resolvedStyle!)
                    {
                        fontSize = Mathf.RoundToInt(15f * scaled * rowScale),
                        richText = true
                    };
                    DrawDesktopText(detailRect, detailText, detailStyle, preferBitmapRenderer: true);
                }

                if (!string.IsNullOrWhiteSpace(scoreText))
                {
                    rowValueStyle!.normal.textColor = row.IsLocalPlayer ? new Color32(0x00, 0xC8, 0xFF, 0xFF) : Color.white;
                    DrawDesktopText(
                        new Rect(scoreRectX, rowY, scoreWidth, rowHeight),
                        scoreText,
                        rowValueStyle);
                }

                y += rowHeight + rowGap;
                continue;
            }

            var rowValueParts = BuildDesktopRowValueParts(currentConfig, currentViewModel, row, useExpandedSettings);
            var rowValueWidth = rowValueParts.Count == 0 ? 0f : Math.Max(116f * scaled, MeasureDesktopRowValuePartsWidth(rowValueStyle!, rowValueParts, scaled) + (12f * scaled));
            var valueRectX = rect.x + rect.width - valueColumnInset - rowValueWidth;
            var beatLeaderDataGap = 42f * scaled;
            var rowTextWidth = Math.Max(80f * scaled, valueRectX - rowX - beatLeaderDataGap);
            if (showExpandedDetails && showNames)
            {
                DrawDesktopText(
                    new Rect(rowX + (2f * scaled), rowY, rowTextWidth, rowHeight),
                    rowName,
                    resolvedStyle);
            }

            if (rowValueParts.Count > 0 && (showExpandedDetails || showPlayerCollapsedValueOnly))
            {
                DrawDesktopRowValues(
                    new Rect(valueRectX, rowY, rowValueWidth, rowHeight),
                    rowValueParts,
                    rowValueStyle!,
                    scaled);
            }
            y += rowHeight + rowGap;
        }

        GUI.matrix = previousMatrix;
    }

    private void EnsureWorldOverlay()
    {
        if (IsWorldOverlayUsable())
        {
            return;
        }

        ResetWorldOverlay();

        worldRoot = new GameObject("BeatRelayWorldPanel");
        UnityEngine.Object.DontDestroyOnLoad(worldRoot);
        worldRoot.SetActive(false);
        worldLines = new TMP_Text[MaxWorldLines];
        worldNameLines = new TMP_Text[MaxWorldLines];
        worldValueLines = new TMP_Text[MaxWorldLines];
        worldRowContainers = new GameObject[MaxWorldLines];
        worldPanelBackgroundTexture = MakeTexture(WorldBackgroundTextureColor);
        worldHighlightTexture = MakeTexture(WorldLocalHighlightColor);

        worldBackgroundObject = CreateWorldQuad("Background", worldRoot.transform, worldPanelBackgroundTexture, new Color(1f, 1f, 1f, 0.88f), -20);
        worldHighlightObject = CreateWorldQuad("LocalHighlight", worldRoot.transform, worldHighlightTexture, Color.white, -10);
        worldHighlightObject.SetActive(false);

        for (var i = 0; i < worldLines.Length; i++)
        {
            var rowContainer = new GameObject("Row" + i);
            rowContainer.transform.SetParent(worldRoot.transform, false);
            rowContainer.transform.localPosition = Vector3.zero;
            worldRowContainers[i] = rowContainer;

            var lineObject = new GameObject("Line" + i);
            lineObject.transform.SetParent(rowContainer.transform, false);
            lineObject.transform.localPosition = new Vector3(0f, 0f, WorldTextZ);
            var textMesh = lineObject.AddComponent<TextMeshPro>();
            var rectTransform = textMesh.rectTransform;
            rectTransform.anchorMin = new Vector2(0f, 1f);
            rectTransform.anchorMax = new Vector2(0f, 1f);
            rectTransform.pivot = new Vector2(0f, 1f);
            rectTransform.sizeDelta = new Vector2(1.35f, 0.12f);
            textMesh.alignment = TextAlignmentOptions.TopLeft;
            textMesh.fontSize = i == 1 ? WorldRankFontSize : WorldBodyFontSize;
            SetTextWrapping(textMesh, noWrap: true);
            textMesh.overflowMode = TextOverflowModes.Overflow;
            textMesh.richText = false;
            textMesh.color = Color.white;
            textMesh.raycastTarget = false;
            ConfigureWorldTextRenderer(textMesh);
            worldLines[i] = textMesh;

            var nameObject = new GameObject("Line" + i + "Name");
            nameObject.transform.SetParent(rowContainer.transform, false);
            nameObject.transform.localPosition = new Vector3(WorldRowNameX, 0f, WorldTextZ);
            var nameText = nameObject.AddComponent<TextMeshPro>();
            var nameRectTransform = nameText.rectTransform;
            nameRectTransform.anchorMin = new Vector2(0f, 1f);
            nameRectTransform.anchorMax = new Vector2(0f, 1f);
            nameRectTransform.pivot = new Vector2(0f, 1f);
            nameRectTransform.sizeDelta = new Vector2(0.46f, 0.12f);
            nameText.alignment = TextAlignmentOptions.TopLeft;
            nameText.fontSize = WorldBodyFontSize;
            SetTextWrapping(nameText, noWrap: true);
            nameText.overflowMode = TextOverflowModes.Ellipsis;
            nameText.richText = false;
            nameText.color = Color.white;
            nameText.raycastTarget = false;
            ConfigureWorldTextRenderer(nameText);
            nameText.gameObject.SetActive(false);
            worldNameLines[i] = nameText;

            var valueObject = new GameObject("Line" + i + "Value");
            valueObject.transform.SetParent(rowContainer.transform, false);
            valueObject.transform.localPosition = new Vector3(WorldRowExpandedValueX, 0f, WorldTextZ);
            var valueText = valueObject.AddComponent<TextMeshPro>();
            var valueRectTransform = valueText.rectTransform;
            valueRectTransform.anchorMin = new Vector2(0f, 1f);
            valueRectTransform.anchorMax = new Vector2(0f, 1f);
            valueRectTransform.pivot = new Vector2(1f, 1f);
            valueRectTransform.sizeDelta = new Vector2(0.26f, 0.12f);
            valueText.alignment = TextAlignmentOptions.TopRight;
            valueText.fontSize = WorldBodyFontSize;
            SetTextWrapping(valueText, noWrap: true);
            valueText.overflowMode = TextOverflowModes.Overflow;
            valueText.richText = true;
            valueText.color = GetValueTextColor(isRanked: true);
            valueText.raycastTarget = false;
            ConfigureWorldTextRenderer(valueText);
            valueText.gameObject.SetActive(false);
            worldValueLines[i] = valueText;
        }

        ApplyWorldFont();
    }

    private bool IsWorldOverlayUsable()
    {
        if (!IsUnityObjectAlive(worldRoot)
            || !IsUnityObjectAlive(worldBackgroundObject)
            || !IsUnityObjectAlive(worldHighlightObject)
            || worldLines == null
            || worldNameLines == null
            || worldValueLines == null
            || worldRowContainers == null
            || worldLines.Length != MaxWorldLines
            || worldNameLines.Length != MaxWorldLines
            || worldValueLines.Length != MaxWorldLines
            || worldRowContainers.Length != MaxWorldLines)
        {
            return false;
        }

        for (var i = 0; i < MaxWorldLines; i++)
        {
            if (!IsUnityObjectAlive(worldLines[i])
                || !IsUnityObjectAlive(worldNameLines[i])
                || !IsUnityObjectAlive(worldValueLines[i])
                || !IsUnityObjectAlive(worldRowContainers[i]))
            {
                return false;
            }
        }

        return true;
    }

    private void ResetWorldOverlay()
    {
        worldTextWidths.Clear();
        worldValueReveals.Clear();
        if (IsUnityObjectAlive(worldRoot))
        {
            UnityEngine.Object.Destroy(worldRoot);
        }

        worldRoot = null;
        worldLines = null;
        worldNameLines = null;
        worldValueLines = null;
        worldBackgroundObject = null;
        worldHighlightObject = null;
        worldRowContainers = null;
        DestroyTexture(ref worldPanelBackgroundTexture);
        DestroyTexture(ref worldHighlightTexture);
        worldTextPrewarmed = false;
        forceWorldTextRefresh = true;
    }

    private static bool IsUnityObjectAlive(UnityEngine.Object? unityObject)
    {
        return unityObject != null;
    }

    private void HideWorldOverlay()
    {
        if (worldLines != null)
        {
            for (var i = 0; i < worldLines.Length; i++)
            {
                var line = worldLines[i];
                if (line == null)
                {
                    continue;
                }

                ClearAndHideWorldLine(line);
            }
        }

        if (worldValueLines != null)
        {
            for (var i = 0; i < worldValueLines.Length; i++)
            {
                var line = worldValueLines[i];
                if (line == null)
                {
                    continue;
                }

                ClearAndHideWorldLine(line);
            }
        }

        if (worldNameLines != null)
        {
            for (var i = 0; i < worldNameLines.Length; i++)
            {
                var line = worldNameLines[i];
                if (line == null)
                {
                    continue;
                }

                ClearAndHideWorldLine(line);
            }
        }

        if (worldBackgroundObject != null)
        {
            SetActiveIfChanged(worldBackgroundObject, false);
        }

        if (worldHighlightObject != null)
        {
            SetActiveIfChanged(worldHighlightObject, false);
        }

        if (worldRoot != null)
        {
            SetActiveIfChanged(worldRoot, false);
        }
    }

    private void PositionWorldPanel(OverlayConfig currentConfig)
    {
        if (worldRoot == null)
        {
            return;
        }

        if (worldRoot.transform.parent != null)
        {
            worldRoot.transform.SetParent(null, true);
        }

        if (TryResolveCountersPlusWorldPose(currentConfig.PositionPreset, out var countersPlusPosition, out var countersPlusRotation))
        {
            worldRoot.transform.position = countersPlusPosition;
            worldRoot.transform.rotation = countersPlusRotation;
        }
        else
        {
            worldRoot.transform.position = GetWorldPosition(currentConfig);
            worldRoot.transform.rotation = GetWorldRotation(currentConfig);
        }

        worldRoot.transform.localScale = Vector3.one * (float)Math.Max(1.0, Math.Min(4.0, currentConfig.GetEffectiveInGameOverlayScale()));
    }

    private void PositionWorldPanelForCustomizationPreview(OverlayConfig currentConfig)
    {
        if (worldRoot == null)
        {
            return;
        }

        if (worldRoot.transform.parent != null)
        {
            worldRoot.transform.SetParent(null, true);
        }

        worldRoot.transform.position = new Vector3(0f, 1.70f, 3.35f);
        worldRoot.transform.rotation = Quaternion.Euler(0f, 0f, 0f);
        worldRoot.transform.localScale = Vector3.one * (float)Math.Max(1.0, Math.Min(4.0, currentConfig.GetEffectiveInGameOverlayScale()));
    }

    private static Vector3 GetWorldPosition(OverlayConfig currentConfig)
    {
        if (TryResolveWorldPreset(currentConfig.PositionPreset, out var presetPosition, out _))
        {
            return presetPosition;
        }

        TryResolveWorldPreset(DefaultWorldPositionPreset, out presetPosition, out _);
        return presetPosition;
    }

    private static Quaternion GetWorldRotation(OverlayConfig currentConfig)
    {
        if (TryResolveWorldPreset(currentConfig.PositionPreset, out _, out var presetRotation))
        {
            return presetRotation;
        }

        TryResolveWorldPreset(DefaultWorldPositionPreset, out _, out presetRotation);
        return presetRotation;
    }

    private bool TryResolveCountersPlusWorldPose(string preset, out Vector3 position, out Quaternion rotation)
    {
        position = default;
        rotation = default;

        if (!TryGetCachedBaseHudPose(out var baseHud, out var hudWidth, out var hudHeight, out var hudDepth))
        {
            return false;
        }

        var anchor = ResolveCountersPlusAnchor(preset, hudWidth, hudHeight);
        var origin = baseHud.position + (baseHud.forward * hudDepth);
        origin.y = baseHud.position.y + hudHeight;
        position = origin + (baseHud.right * anchor.x) + (Vector3.up * anchor.y);
        rotation = baseHud.rotation;

        if (!countersPlusPositionLogged)
        {
            countersPlusPositionLogged = true;
            logger?.Info(
                "runtime_countersplus_position_resolved",
                $"preset={preset}; hudWidth={hudWidth:0.###}; hudHeight={hudHeight:0.###}; hudDepth={hudDepth:0.###}; anchor=({anchor.x:0.###},{anchor.y:0.###}); world=({position.x:0.###},{position.y:0.###},{position.z:0.###})");
        }

        return true;
    }

    private bool TryGetCachedBaseHudPose(out Transform baseHud, out float hudWidth, out float hudHeight, out float hudDepth)
    {
        if (cachedCountersPlusBaseHud != null)
        {
            baseHud = cachedCountersPlusBaseHud;
            hudWidth = cachedCountersPlusHudWidth;
            hudHeight = cachedCountersPlusHudHeight;
            hudDepth = cachedCountersPlusHudDepth;
            return true;
        }

        var now = Time.unscaledTime;
        if (now < nextCountersPlusHudScanTime)
        {
            baseHud = null!;
            hudWidth = cachedCountersPlusHudWidth;
            hudHeight = cachedCountersPlusHudHeight;
            hudDepth = cachedCountersPlusHudDepth;
            return false;
        }

        nextCountersPlusHudScanTime = now + CountersPlusHudScanIntervalSeconds;
        if (!TryFindBaseHudPose(out baseHud, out hudWidth, out hudHeight, out hudDepth))
        {
            return false;
        }

        cachedCountersPlusBaseHud = baseHud;
        cachedCountersPlusHudWidth = hudWidth;
        cachedCountersPlusHudHeight = hudHeight;
        cachedCountersPlusHudDepth = hudDepth;
        return true;
    }

    private static bool TryFindBaseHudPose(out Transform baseHud, out float hudWidth, out float hudHeight, out float hudDepth)
    {
        baseHud = null!;
        hudWidth = 3.2f;
        hudHeight = 0f;
        hudDepth = 7f;

        MonoBehaviour? coreHud = null;
        foreach (var behaviour in Resources.FindObjectsOfTypeAll<MonoBehaviour>())
        {
            if (behaviour == null || behaviour.gameObject == null)
            {
                continue;
            }

            if (behaviour.GetType().Name.IndexOf("CoreGameHUDController", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                coreHud = behaviour;
                break;
            }
        }

        if (coreHud == null)
        {
            return false;
        }

        baseHud = coreHud.transform;
        foreach (var behaviour in coreHud.GetComponentsInChildren<MonoBehaviour>(true))
        {
            if (behaviour == null)
            {
                continue;
            }

            if (behaviour.GetType().Name.IndexOf("ComboUIController", StringComparison.OrdinalIgnoreCase) < 0)
            {
                continue;
            }

            var comboPosition = behaviour.transform.position;
            hudWidth = Mathf.Abs(comboPosition.x);
            hudHeight = comboPosition.y;
            hudDepth = comboPosition.z;
            break;
        }

        return true;
    }

    private static Vector2 ResolveCountersPlusAnchor(string preset, float hudWidth, float hudHeight)
    {
        const float ComboOffset = 0f;
        const float MultiplierOffset = 0f;
        const float BelowEnergyOffset = -1.5f;
        const float AboveHighwayOffset = 0.75f;
        const int BeatRelayDistance = -1;
        const float DistanceModifier = 1f;

        var normalized = string.IsNullOrWhiteSpace(preset) ? "AboveMultiplier" : preset.Trim();
        var offsetY = -0.75f * (BeatRelayDistance * DistanceModifier);
        var x = hudWidth <= 0.001f ? 3.2f : hudWidth;
        var y = 0f;

        switch (normalized)
        {
            case "BelowCombo":
                x = -x;
                y = 1.15f - ComboOffset + offsetY;
                break;
            case "AboveCombo":
                x = -x;
                y = 2f + ComboOffset + ((offsetY * -1f) + 0.75f);
                break;
            case "BelowMultiplier":
                y = 1.05f - MultiplierOffset + offsetY;
                break;
            case "AboveMultiplier":
            case "ReplaceMultiplier":
                y = 2f + MultiplierOffset + ((offsetY * -1f) + 0.75f);
                break;
            case "BelowEnergy":
            case "UnderEnergy":
                x = 0f;
                y = BelowEnergyOffset + offsetY;
                break;
            case "AboveHighway":
            case "AboveLane":
                x = 0f;
                y = 2.5f + ((offsetY * -1f) + AboveHighwayOffset);
                break;
            default:
                y = 2f + MultiplierOffset + ((offsetY * -1f) + 0.75f);
                break;
        }

        return new Vector2(x, y - hudHeight);
    }

    private static bool TryResolveWorldPreset(string preset, out Vector3 position, out Quaternion rotation)
    {
        var normalized = string.IsNullOrWhiteSpace(preset) ? "AboveMultiplier" : preset.Trim();
        switch (normalized)
        {
            case "AboveMultiplier":
            case "ReplaceMultiplier":
                position = new Vector3(1.55f, 1.82f, 1.65f);
                rotation = Quaternion.Euler(4f, -18f, 0f);
                return true;
            case "BelowMultiplier":
                position = new Vector3(1.55f, 1.18f, 1.65f);
                rotation = Quaternion.Euler(-2f, -18f, 0f);
                return true;
            case "UnderEnergy":
            case "BelowEnergy":
                position = new Vector3(0f, 0.76f, 1.86f);
                rotation = Quaternion.Euler(-3f, 0f, 0f);
                return true;
            case "BelowCombo":
                position = new Vector3(-1.28f, 1.16f, 1.78f);
                rotation = Quaternion.Euler(-2f, 16f, 0f);
                return true;
            case "AboveCombo":
                position = new Vector3(-1.28f, 1.78f, 1.78f);
                rotation = Quaternion.Euler(4f, 16f, 0f);
                return true;
            case "AboveScore":
                position = new Vector3(0f, 2.06f, 1.86f);
                rotation = Quaternion.Euler(5f, 0f, 0f);
                return true;
            case "AboveLane":
            case "AboveHighway":
                position = new Vector3(0f, 1.34f, 2.18f);
                rotation = Quaternion.Euler(1f, 0f, 0f);
                return true;
            case "RightSide":
                position = new Vector3(2.12f, 1.36f, 1.08f);
                rotation = Quaternion.Euler(0f, -40f, 0f);
                return true;
            default:
                position = new Vector3(1.05f, 1.4f, 1.2f);
                rotation = Quaternion.Euler(1f, 25f, 0f);
                return true;
        }
    }

    private void ApplyWorldText(OverlayConfig currentConfig, OverlayViewModel currentViewModel)
    {
        if (worldLines == null || worldNameLines == null || worldValueLines == null) return;

        var progress = GetEasedExpansionBlend();
        var collapsedScale = (float)currentConfig.GetEffectivePlayerRowScale(false);
        var expandedScale = (float)currentConfig.GetEffectivePlayerRowScale(true);
        var rowScale = Mathf.Lerp(collapsedScale, expandedScale, progress);
        var collapsedBigRank = currentConfig.GetShowBigRank(false);
        var expandedBigRank = currentConfig.GetShowBigRank(true);
        var reserveBigRank = collapsedBigRank || expandedBigRank;
        var bigRankPresence = Mathf.Lerp(collapsedBigRank ? 1f : 0f, expandedBigRank ? 1f : 0f, progress);
        var bigRankScale = Mathf.Lerp((float)currentConfig.GetBigRankScale(false), (float)currentConfig.GetBigRankScale(true), progress);
        var rowStartIndex = reserveBigRank ? 1 : 0;
        var rowCount = Math.Min(currentViewModel.Rows.Count, worldLines.Length - rowStartIndex);
        var collapsedFields = new string[rowCount][];
        var expandedFields = new string[rowCount][];
        var collapsedWidths = new double[6];
        var expandedWidths = new double[6];
        for (var r = 0; r < rowCount; r++)
        {
            var row = currentViewModel.Rows[r];
            collapsedFields[r] = BuildWorldFields(currentConfig, currentViewModel, row, false);
            expandedFields[r] = BuildWorldFields(currentConfig, currentViewModel, row, true);
            for (var column = 0; column < 6; column++)
            {
                var measureLine = column == 0 ? worldLines[r + rowStartIndex]
                    : column == 1 ? worldNameLines[r + rowStartIndex] : worldValueLines[r + rowStartIndex];
                // Measure each enabled column across the visible leaderboard, including
                // collapsed-hidden opponents, so identical settings keep identical geometry.
                collapsedWidths[column] = Math.Max(collapsedWidths[column], MeasureWorldText(measureLine, collapsedFields[r][column], WorldBodyFontSize * collapsedScale, richText: column >= 2));
                expandedWidths[column] = Math.Max(expandedWidths[column], MeasureWorldText(measureLine, expandedFields[r][column], WorldBodyFontSize * expandedScale, richText: column >= 2));
            }
        }

        const double padding = 0.055d;
        var collapsedLayout = new OverlayContentLayout(collapsedWidths, 0.022d * collapsedScale, padding);
        var expandedLayout = new OverlayContentLayout(expandedWidths, 0.022d * expandedScale, padding);
        var rankText = GetDisplayedWorldRankText(currentViewModel);
        var collapsedWidth = (float)collapsedLayout.Width;
        var expandedWidth = (float)expandedLayout.Width;
        if (collapsedBigRank) collapsedWidth = Mathf.Max(collapsedWidth, MeasureWorldText(worldLines[0], rankText, WorldRankFontSize * (float)currentConfig.GetBigRankScale(false), true) + (float)(2d * padding));
        if (expandedBigRank) expandedWidth = Mathf.Max(expandedWidth, MeasureWorldText(worldLines[0], rankText, WorldRankFontSize * (float)currentConfig.GetBigRankScale(true), true) + (float)(2d * padding));
        var width = Mathf.Lerp(collapsedWidth, expandedWidth, progress);
        var left = -collapsedWidth * 0.5f;
        var center = left + width * 0.5f;
        var rowSpacing = WorldLineSpacing * Mathf.Max(1f, rowScale);
        var bigRankOffset = (rowSpacing + Mathf.Max(0f, bigRankScale - 1f) * 0.13f) * bigRankPresence;
        var height = 0.20f + bigRankOffset + (Math.Max(rowCount, 2) * 0.078f * Mathf.Max(1f, rowScale));
        var scoreSaber = IsScoreSaberViewModel(currentViewModel);
        if (worldBackgroundObject != null)
        {
            SetActiveIfChanged(worldBackgroundObject, true);
            var opacity = Mathf.Lerp(GetBackgroundOpacity01(currentConfig, false), GetBackgroundOpacity01(currentConfig, true), progress);
            ApplyWorldQuadColor(worldBackgroundObject, new Color(1f, 1f, 1f, opacity));
            worldBackgroundObject.transform.localPosition = new Vector3(center, -(height * 0.5f) + 0.03f, WorldBackgroundZ);
            worldBackgroundObject.transform.localScale = new Vector3(width, height, 1f);
        }

        var foundHighlight = false;
        for (var i = 0; i < worldLines.Length; i++)
        {
            var rankLine = worldLines[i];
            var nameLine = worldNameLines[i];
            var valueLine = worldValueLines[i];
            if (worldRowContainers != null) worldRowContainers[i].transform.localPosition = Vector3.zero;
            if (reserveBigRank && i == 0)
            {
                SetWorldColumn(rankLine, rankText, left + (float)padding, 0f, WorldRankFontSize * bigRankScale, new Color(1f, 1f, 1f, bigRankPresence), true);
                ClearAndHideWorldLine(nameLine);
                ClearAndHideWorldLine(valueLine);
                continue;
            }

            var rowIndex = i - rowStartIndex;
            if (rowIndex >= rowCount)
            {
                ClearAndHideWorldLine(rankLine);
                ClearAndHideWorldLine(nameLine);
                ClearAndHideWorldLine(valueLine);
                continue;
            }

            var row = currentViewModel.Rows[rowIndex];
            var from = collapsedFields[rowIndex];
            var to = expandedFields[rowIndex];
            var y = -rowSpacing * rowIndex - bigRankOffset;
            if (worldRowContainers != null) worldRowContainers[i].transform.localPosition = new Vector3(0f, y, 0f);
            var textY = WorldRowTextYOffset * Mathf.Max(1f, rowScale);
            Color baseColor = scoreSaber && row.IsLocalPlayer ? new Color32(0x00, 0xC8, 0xFF, 0xFF)
                : row.IsLocalPlayer ? Color.white : new Color(0.9f, 0.95f, 1f, 1f);
            var rankX = left + (float)OverlayContentLayout.InterpolatePosition(collapsedLayout.Positions[0], expandedLayout.Positions[0], progress);
            SetWorldColumn(rankLine, row.Rank.ToString(), rankX, textY, WorldBodyFontSize * rowScale, baseColor);
            var nameAlpha = Mathf.Lerp(string.IsNullOrEmpty(from[1]) ? 0f : 1f, string.IsNullOrEmpty(to[1]) ? 0f : 1f, progress);
            var nameX = left + (float)OverlayContentLayout.InterpolatePosition(collapsedLayout.Positions[1], expandedLayout.Positions[1], progress);
            SetWorldColumn(nameLine, string.IsNullOrEmpty(to[1]) ? from[1] : to[1], nameX, textY, WorldBodyFontSize * rowScale, new Color(baseColor.r, baseColor.g, baseColor.b, nameAlpha));

            if (!worldValueReveals.TryGetValue(valueLine, out var reveal))
            {
                reveal = new WorldValueReveal();
                worldValueReveals.Add(valueLine, reveal);
                var capturedReveal = reveal;
                // TMP can rebuild later in the frame. Apply the mask to every fresh mesh,
                // before upload, rather than leaving a one-frame unmasked flash.
                valueLine.OnPreRenderText += info => ApplyWorldValueReveal(info, capturedReveal);
            }

            reveal.Spans.Clear();
            reveal.Progress = (float)OverlayContentLayout.Reveal(progress);
            reveal.FeatherWidth = 0.009f * rowScale;
            var builder = new StringBuilder();
            for (var column = 2; column < 6; column++)
            {
                var wasVisible = row.IsLocalPlayer && !string.IsNullOrEmpty(from[column]);
                var willBeVisible = !string.IsNullOrEmpty(to[column]);
                if (!wasVisible && !willBeVisible) continue;
                var columnText = willBeVisible ? to[column] : from[column];
                var x = OverlayContentLayout.InterpolatePosition(collapsedLayout.Positions[column], expandedLayout.Positions[column], progress);
                builder.Append("<pos=").Append((100d * x / width).ToString("0.#####", System.Globalization.CultureInfo.InvariantCulture)).Append("%>");
                var spanStart = builder.Length;
                builder.Append(columnText);
                reveal.Spans.Add(new WorldValueSpan(spanStart, builder.Length, (float)OverlayContentLayout.FieldOpacity(wasVisible, willBeVisible, progress), !wasVisible && willBeVisible));
            }

            valueLine.richText = true;
            valueLine.rectTransform.sizeDelta = new Vector2(width, 0.12f);
            SetWorldColumn(valueLine, builder.ToString(), left, textY, WorldBodyFontSize * rowScale, scoreSaber ? baseColor : Color.white, richText: true);
            if (valueLine.gameObject.activeSelf) valueLine.ForceMeshUpdate(ignoreActiveState: false, forceTextReparsing: true);

            var highlightPresence = Mathf.Lerp(currentConfig.GetShowHighlight(false) ? 1f : 0f, currentConfig.GetShowHighlight(true) ? 1f : 0f, progress);
            if (!scoreSaber && row.IsLocalPlayer && worldHighlightObject != null && highlightPresence > 0.001f)
            {
                foundHighlight = true;
                SetActiveIfChanged(worldHighlightObject, true);
                worldHighlightObject.transform.localPosition = new Vector3(center, y - 0.034f * Mathf.Max(1f, rowScale), WorldHighlightZ);
                worldHighlightObject.transform.localScale = new Vector3(width - 0.03f, 0.074f * Mathf.Max(1f, rowScale), 1f);
                ApplyLocalHighlightVisual(worldHighlightObject, currentViewModel.NoFailPenaltyActive, highlightPresence);
            }
        }

        if (!foundHighlight && worldHighlightObject != null) SetActiveIfChanged(worldHighlightObject, false);
    }

    private float MeasureWorldText(TMP_Text line, string text, float size, bool bold = false, bool richText = false)
    {
        if (string.IsNullOrEmpty(text)) return 0f;
        var key = $"{line.font?.GetInstanceID()}|{size:R}|{bold}|{richText}|{text}";
        if (worldTextWidths.TryGetValue(key, out var width)) return width;
        line.fontSize = size;
        line.fontStyle = bold ? FontStyles.Bold : FontStyles.Italic;
        line.richText = richText;
        width = line.GetPreferredValues(text, float.PositiveInfinity, float.PositiveInfinity).x;
        if (worldTextWidths.Count >= 1024) worldTextWidths.Clear();
        worldTextWidths[key] = width;
        return width;
    }

    private static void SetWorldColumn(TMP_Text line, string text, float x, float y, float size, Color color, bool bold = false, bool richText = false)
    {
        if (string.IsNullOrEmpty(text) || color.a <= 0f)
        {
            ClearAndHideWorldLine(line);
            return;
        }

        SetActiveIfChanged(line.gameObject, true);
        line.transform.localPosition = new Vector3(x, y, WorldTextZ);
        line.fontSize = size;
        line.color = color;
        line.richText = richText;
        line.fontStyle = bold ? FontStyles.Bold : FontStyles.Italic;
        SetStableWorldLineText(line, text, TextAlignmentOptions.TopLeft, TextOverflowModes.Overflow, noWrap: true);
    }

    private static string[] BuildWorldFields(OverlayConfig config, OverlayViewModel model, OverlayRowViewModel row, bool expanded)
    {
        var ss = IsScoreSaberViewModel(model);
        var fields = new string[6];
        fields[0] = row.Rank.ToString();
        fields[1] = config.GetShowNames(expanded) ? ClipOverlayPlayerName(row.PlayerName) : string.Empty;
        if (config.GetShowModifiers(expanded))
        {
            fields[ss ? 4 : 2] = ss
                ? ColorizeRowValue("#4E4E56", string.Join("  ", NormalizeScoreSaberModifiers(row.Modifiers, includeNoFail: true).Select(modifier => $"[{modifier}]")))
                : ColorizeRowValue("#8C8C96", BuildBeatLeaderModifierText(row.Modifiers, includeNoFail: model.NoFailPenaltyActive || !row.IsLocalPlayer));
        }
        if (config.GetShowAccuracy(expanded) && row.Accuracy.HasValue)
            fields[ss ? 2 : 3] = ss ? $"(<color=#FFD52A>{FormatAccuracy(row.Accuracy.Value)}</color>)" : ColorizeRowValue("#FFA45A", FormatAccuracy(row.Accuracy.Value));
        if (ShouldShowPpValue(config, model, expanded) && !string.IsNullOrWhiteSpace(row.ValueText))
            fields[ss ? 3 : 4] = ss ? $"(<color=#6872E5>{FormatScoreSaberPp(row.ValueText, true)}</color>)" : ColorizeRowValue("#B75CFF", row.ValueText);
        if (ShouldShowScoreValue(config, model, expanded))
        {
            var score = row.Score ?? (row.IsLocalPlayer ? model.ProjectedScore : null);
            if (score.HasValue && (!ss || score.Value > 0)) fields[5] = ColorizeRowValue(ss && row.IsLocalPlayer ? "#00C8FF" : "#FFFFFF", score.Value.ToString("N0"));
        }
        return fields;
    }

    private sealed class WorldValueReveal
    {
        public readonly List<WorldValueSpan> Spans = new();
        public float Progress;
        public float FeatherWidth;
    }

    private readonly struct WorldValueSpan
    {
        public WorldValueSpan(int start, int end, float opacity, bool incoming)
        { Start = start; End = end; Opacity = opacity; Incoming = incoming; }
        public readonly int Start;
        public readonly int End;
        public readonly float Opacity;
        public readonly bool Incoming;
        public bool Contains(int index) => index >= Start && index < End;
    }

    private static void ApplyWorldValueReveal(TMP_TextInfo info, WorldValueReveal reveal)
    {
        var min = float.PositiveInfinity;
        var max = float.NegativeInfinity;
        for (var i = 0; i < info.characterCount; i++)
        {
            var character = info.characterInfo[i];
            if (!character.isVisible) continue;
            foreach (var span in reveal.Spans)
            {
                if (!span.Incoming || !span.Contains(character.index)) continue;
                var vertices = info.meshInfo[character.materialReferenceIndex].vertices;
                for (var v = 0; v < 4; v++)
                {
                    min = Mathf.Min(min, vertices[character.vertexIndex + v].x);
                    max = Mathf.Max(max, vertices[character.vertexIndex + v].x);
                }
            }
        }

        var edge = Mathf.Lerp(min - reveal.FeatherWidth, max + reveal.FeatherWidth, reveal.Progress);
        for (var i = 0; i < info.characterCount; i++)
        {
            var character = info.characterInfo[i];
            if (!character.isVisible) continue;
            foreach (var span in reveal.Spans)
            {
                if (!span.Contains(character.index)) continue;
                var mesh = info.meshInfo[character.materialReferenceIndex];
                var first = character.vertexIndex;
                for (var v = 0; v < 4; v++)
                {
                    var color = mesh.colors32[first + v];
                    color.a = (byte)Mathf.RoundToInt(color.a * span.Opacity);
                    mesh.colors32[first + v] = color;
                }
                if (span.Incoming && reveal.Progress < 1f)
                {
                    // Clip each horizontal edge, including its UVs, so a glyph is
                    // uncovered left to right without stretching the hidden portion.
                    ClipWorldGlyphEdge(mesh, first, first + 3, edge);
                    ClipWorldGlyphEdge(mesh, first + 1, first + 2, edge);
                    for (var v = 0; v < 4; v++)
                    {
                        var color = mesh.colors32[first + v];
                        color.a = (byte)Mathf.RoundToInt(color.a * Mathf.Clamp01((edge - mesh.vertices[first + v].x) / reveal.FeatherWidth));
                        mesh.colors32[first + v] = color;
                    }
                }
                break;
            }
        }
    }

    private static void ClipWorldGlyphEdge(TMP_MeshInfo mesh, int left, int right, float edge)
    {
        var a = mesh.vertices[left];
        var b = mesh.vertices[right];
        if (b.x <= edge) return;
        var fraction = b.x > a.x ? Mathf.Clamp01((edge - a.x) / (b.x - a.x)) : 0f;
        mesh.vertices[right] = a + (b - a) * fraction;
        mesh.uvs0[right] = mesh.uvs0[left] + (mesh.uvs0[right] - mesh.uvs0[left]) * fraction;
        mesh.uvs2[right] = mesh.uvs2[left] + (mesh.uvs2[right] - mesh.uvs2[left]) * fraction;
    }

    private void ApplyLocalHighlightVisual(GameObject highlightObject, bool noFailPenaltyActive, float opacity)
    {
        var renderer = highlightObject.GetComponent<MeshRenderer>();
        if (renderer == null || renderer.sharedMaterial == null)
        {
            return;
        }

        if (!noFailPenaltyActive || !noFailHighlightWaveStartTime.HasValue)
        {
            renderer.sharedMaterial.mainTexture = worldHighlightTexture;
            renderer.sharedMaterial.color = new Color(1f, 1f, 1f, opacity);
            return;
        }

        var elapsed = Time.time - noFailHighlightWaveStartTime.Value;
        if (elapsed >= NoFailHighlightWaveSeconds)
        {
            renderer.sharedMaterial.mainTexture = worldHighlightTexture;
            renderer.sharedMaterial.color = new Color(1f, 1f, 1f, opacity);
            return;
        }

        renderer.sharedMaterial.mainTexture = UpdateNoFailHighlightTexture(Mathf.Clamp01(elapsed / NoFailHighlightWaveSeconds));
        renderer.sharedMaterial.color = new Color(1f, 1f, 1f, opacity);
    }

    private Texture2D UpdateNoFailHighlightTexture(float phase)
    {
        const int Width = 64;
        if (worldNoFailHighlightTexture == null)
        {
            worldNoFailHighlightTexture = new Texture2D(Width, 1, TextureFormat.RGBA32, mipChain: false)
            {
                name = "BL_NoFailHighlightWave",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear
            };
        }

        var center = Mathf.Lerp(-0.20f, 1.20f, phase);
        var pixels = new Color[Width];
        for (var x = 0; x < Width; x++)
        {
            var t = Width <= 1 ? 0f : (float)x / (Width - 1);
            var intensity = Mathf.Clamp01(1f - (Mathf.Abs(t - center) / 0.22f));
            intensity *= Mathf.Sin(phase * Mathf.PI);
            pixels[x] = Color.Lerp(WorldLocalHighlightColor, WorldNoFailHighlightColor, intensity);
        }

        worldNoFailHighlightTexture.SetPixels(pixels);
        worldNoFailHighlightTexture.Apply(updateMipmaps: false, makeNoLongerReadable: false);
        return worldNoFailHighlightTexture;
    }

    private void PrewarmWorldTextMeshes()
    {
        if (worldLines == null)
        {
            return;
        }

        for (var i = 0; i < worldLines.Length; i++)
        {
            var line = worldLines[i];
            if (line == null)
            {
                continue;
            }

            line.gameObject.SetActive(true);
            line.text = " ";
            ApplyWorldLineTypography(line, i <= 1);
            line.ForceMeshUpdate(ignoreActiveState: true, forceTextReparsing: true);
        }

        if (worldNameLines != null)
        {
            for (var i = 0; i < worldNameLines.Length; i++)
            {
                var line = worldNameLines[i];
                if (line == null)
                {
                    continue;
                }

                line.gameObject.SetActive(true);
                line.text = " ";
                ApplyWorldLineTypography(line, false);
                line.ForceMeshUpdate(ignoreActiveState: true, forceTextReparsing: true);
            }
        }

        if (worldValueLines != null)
        {
            for (var i = 0; i < worldValueLines.Length; i++)
            {
                var line = worldValueLines[i];
                if (line == null)
                {
                    continue;
                }

                line.gameObject.SetActive(true);
                line.text = " ";
                ApplyWorldLineTypography(line, false);
                line.ForceMeshUpdate(ignoreActiveState: true, forceTextReparsing: true);
            }
        }
    }

    private static void SetStableWorldLineText(
        TMP_Text line,
        string text,
        TextAlignmentOptions alignment,
        TextOverflowModes overflow,
        bool noWrap)
    {
        if (line == null)
        {
            return;
        }

        var normalizedText = text ?? string.Empty;
        var textChanged = !string.Equals(line.text, normalizedText, StringComparison.Ordinal);
        var desiredWordWrapping = !noWrap;
        var desiredPivot = alignment == TextAlignmentOptions.TopRight
            ? new Vector2(1f, 1f)
            : new Vector2(0f, 1f);
        var rectTransform = line.rectTransform;
        var layoutChanged = line.alignment != alignment
            || line.overflowMode != overflow
            || line.enableWordWrapping != desiredWordWrapping
            || rectTransform.pivot != desiredPivot;

        if (!textChanged && !layoutChanged)
        {
            return;
        }

        if (line.alignment != alignment)
        {
            line.alignment = alignment;
        }

        if (line.overflowMode != overflow)
        {
            line.overflowMode = overflow;
        }

        if (line.enableWordWrapping != desiredWordWrapping)
        {
            line.enableWordWrapping = desiredWordWrapping;
        }

        if (rectTransform.pivot != desiredPivot)
        {
            rectTransform.pivot = desiredPivot;
        }

        if (textChanged)
        {
            line.text = normalizedText;
        }

        line.SetVerticesDirty();
        line.SetLayoutDirty();
    }

    private static void ClearTextIfNeeded(TMP_Text line)
    {
        if (!string.IsNullOrEmpty(line.text))
        {
            line.text = string.Empty;
        }
    }

    private static void ClearAndHideWorldLine(TMP_Text? line)
    {
        if (line == null)
        {
            return;
        }

        ClearTextIfNeeded(line);
        SetActiveIfChanged(line.gameObject, false);
    }

    private static void SetActiveIfChanged(GameObject? gameObject, bool active)
    {
        if (gameObject != null && gameObject.activeSelf != active)
        {
            gameObject.SetActive(active);
        }
    }

    private static string ClipOverlayPlayerName(string name)
    {
        const int MaxNameLength = 15;
        if (string.IsNullOrWhiteSpace(name))
        {
            return string.Empty;
        }

        var trimmed = name.Trim();
        if (trimmed.Equals("built with Codex", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed;
        }

        return trimmed.Length > MaxNameLength
            ? trimmed.Substring(0, MaxNameLength - 3) + "..."
            : trimmed;
    }

    private static void ConfigureWorldTextRenderer(TMP_Text line)
    {
        var renderer = line.GetComponent<MeshRenderer>();
        if (renderer != null)
        {
            renderer.sortingOrder = WorldTextSortingOrder;
        }
    }

    private static void SetTextWrapping(TMP_Text line, bool noWrap)
    {
        line.enableWordWrapping = !noWrap;
    }

    private void EnsureStyles(float scale, float backgroundOpacity)
    {
        backgroundOpacity = Mathf.Clamp01(backgroundOpacity);
        if (panelStyle != null
            && lastConfiguredScale.HasValue
            && lastConfiguredDesktopBackgroundOpacity.HasValue
            && Mathf.Abs(lastConfiguredScale.Value - scale) < 0.001f
            && Mathf.Abs(lastConfiguredDesktopBackgroundOpacity.Value - backgroundOpacity) < 0.001f)
        {
            return;
        }

        lastConfiguredScale = scale;
        lastConfiguredDesktopBackgroundOpacity = backgroundOpacity;
        var titleFont = ResolvePreferredFont(Mathf.RoundToInt(19 * scale), FontStyle.Bold);
        var rankFont = ResolvePreferredFont(Mathf.RoundToInt(43 * scale), FontStyle.Bold);
        var bodyFont = ResolvePreferredFont(Mathf.RoundToInt(14 * scale), FontStyle.Italic);
        var rowFont = ResolvePreferredFont(Mathf.RoundToInt(19 * scale), FontStyle.BoldAndItalic);

        DestroyTexture(ref desktopPanelBackgroundTexture);
        desktopPanelBackgroundTexture = MakeTexture(new Color(0.02f, 0.025f, 0.035f, backgroundOpacity));
        panelStyle = new GUIStyle(GUI.skin.box)
        {
            normal =
            {
                background = desktopPanelBackgroundTexture
            }
        };
        titleStyle = new GUIStyle(GUI.skin.label)
        {
            font = titleFont,
            fontSize = Mathf.RoundToInt(19 * scale),
            fontStyle = SafeGuiFontStyle(titleFont, FontStyle.Bold),
            alignment = TextAnchor.MiddleLeft,
            normal = { textColor = new Color(0.55f, 0.9f, 1f, 1f) }
        };
        rankStyle = new GUIStyle(GUI.skin.label)
        {
            font = rankFont,
            fontSize = Mathf.RoundToInt(43 * scale),
            fontStyle = SafeGuiFontStyle(rankFont, FontStyle.Bold),
            alignment = TextAnchor.MiddleLeft,
            normal = { textColor = Color.white }
        };
        bodyStyle = new GUIStyle(GUI.skin.label)
        {
            font = bodyFont,
            fontSize = Mathf.RoundToInt(14 * scale),
            fontStyle = SafeGuiFontStyle(bodyFont, FontStyle.Italic),
            normal = { textColor = new Color(0.86f, 0.9f, 0.94f, 1f) },
            clipping = TextClipping.Overflow,
            wordWrap = false
        };
        rowStyle = new GUIStyle(GUI.skin.label)
        {
            font = rowFont,
            fontSize = Mathf.RoundToInt(19 * scale),
            fontStyle = SafeGuiFontStyle(rowFont, FontStyle.BoldAndItalic),
            alignment = TextAnchor.MiddleLeft,
            normal = { textColor = Color.white },
            clipping = TextClipping.Clip,
            wordWrap = false
        };
        rowValueStyle = new GUIStyle(rowStyle)
        {
            alignment = TextAnchor.MiddleRight,
            normal = { textColor = GetValueTextColor(isRanked: true) },
            richText = true
        };
        localRowStyle = new GUIStyle(rowStyle)
        {
            normal = { textColor = Color.white },
            fontStyle = SafeGuiFontStyle(rowFont, FontStyle.BoldAndItalic)
        };
        localRowBackgroundTexture = MakeTexture(new Color32(0x2B, 0x0F, 0x40, 0xFF));
        LogDesktopFontSelectionOnce(titleFont, rankFont, bodyFont, rowFont);
    }

    private static FontStyle SafeGuiFontStyle(Font? font, FontStyle desired)
    {
        return font != null && font.dynamic ? desired : FontStyle.Normal;
    }

    private Font? ResolvePreferredFont(int fontSize, FontStyle style)
    {
        var bundled = TekoFontProvider.GetFont(fontSize, style);
        if (bundled != null)
        {
            return bundled;
        }

        EnsurePreferredFontAvailability();
        if (!preferredFontAvailable)
        {
            return ResolveFallbackFont();
        }

        try
        {
            var preferred = Font.CreateDynamicFontFromOSFont(
                new[] { "Teko", "Teko Medium", "Teko SemiBold", "Teko Regular", "Teko-Medium", "Teko-SemiBold", "Teko-Regular" },
                Math.Max(12, fontSize));
            if (preferred != null && preferred.dynamic)
            {
                return preferred;
            }
        }
        catch
        {
        }

        return ResolveFallbackFont();
    }

    private void ApplyWorldFont()
    {
        if (worldLines == null)
        {
            return;
        }

        for (var i = 0; i < worldLines.Length; i++)
        {
            var line = worldLines[i];
            if (line == null)
            {
                continue;
            }

            ApplyWorldLineTypography(line, i <= 1);

            if (worldValueLines != null && i < worldValueLines.Length && worldValueLines[i] != null)
            {
                ApplyWorldLineTypography(worldValueLines[i], false);
            }

            if (worldNameLines != null && i < worldNameLines.Length && worldNameLines[i] != null)
            {
                ApplyWorldLineTypography(worldNameLines[i], false);
            }
        }
    }

    private void ApplyWorldLineTypography(TMP_Text line, bool useBold)
    {
        if (line == null)
        {
            return;
        }

        var desiredStyle = useBold ? FontStyles.Bold : FontStyles.Italic;
        if (line.fontStyle != desiredStyle)
        {
            line.fontStyle = desiredStyle;
        }
    }

    private void ResolveWorldTmpFonts()
    {
        if (worldLinkedFont != null)
        {
            return;
        }

        try
        {
            var allFonts = Resources.FindObjectsOfTypeAll<TMP_FontAsset>();
            // Prefer a linked Medium face so TMP style links can resolve Bold/Italic variants.
            worldLinkedFont ??= allFonts.FirstOrDefault(font =>
                IsTekoWeight(font, "Medium")
                && HasAnyStyleLinks(font)
                && IsUsableTmpFont(font));
            worldLinkedFont ??= allFonts.FirstOrDefault(font => IsTekoWeight(font, "Regular") && HasAnyStyleLinks(font) && IsUsableTmpFont(font));
            worldLinkedFont ??= allFonts.FirstOrDefault(font => IsTekoWeight(font, "Medium") && IsUsableTmpFont(font));

            if (worldLinkedFont == null)
            {
                worldLinkedFont = allFonts.FirstOrDefault(font =>
                    IsTekoFont(font)
                    && IsUsableTmpFont(font)
                    && font.name.IndexOf("Bold", StringComparison.OrdinalIgnoreCase) < 0
                    && font.name.IndexOf("Italic", StringComparison.OrdinalIgnoreCase) < 0);
            }

            if (worldLinkedFont == null)
            {
                var renderedUiText = Resources.FindObjectsOfTypeAll<TextMeshProUGUI>()
                    .FirstOrDefault(text => text != null && text.font != null && text.fontSharedMaterial != null);
                worldLinkedFont = IsUsableTmpFont(renderedUiText?.font) ? renderedUiText?.font : null;
                var uiMaterial = IsCurvedTextMaterial(renderedUiText?.fontSharedMaterial) ? null : renderedUiText?.fontSharedMaterial;
                worldLinkedFontMaterial = CreateReadableFontMaterial(renderedUiText?.font, "Linked", uiMaterial);
            }

            worldLinkedFontMaterial ??= CreateReadableFontMaterial(worldLinkedFont, "Linked");
        }
        catch
        {
            worldLinkedFont = null;
            DestroyWorldFontMaterials();
        }
    }

    private void DestroyWorldFontMaterials()
    {
        if (worldLinkedFontMaterial != null)
        {
            UnityEngine.Object.Destroy(worldLinkedFontMaterial);
            worldLinkedFontMaterial = null;
        }
    }

    private static bool IsTekoWeight(TMP_FontAsset? font, string weight)
    {
        return IsTekoFont(font)
            && font!.name.IndexOf(weight, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static bool IsTekoFont(TMP_FontAsset? font)
    {
        return font != null
            && font.name.IndexOf("Teko", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static bool IsUsableTmpFont(TMP_FontAsset? font)
    {
        try
        {
            return font != null
                && font.atlasTexture != null
                && font.material != null;
        }
        catch
        {
            return false;
        }
    }

    private static bool HasAnyStyleLinks(TMP_FontAsset? font)
    {
        if (font == null)
        {
            return false;
        }

        return font.fontWeightTable.Any(entry => entry.regularTypeface != null || entry.italicTypeface != null);
    }

    private static Material? CreateReadableFontMaterial(TMP_FontAsset? font, string weightName, Material? preferredSourceMaterial = null)
    {
        var sourceMaterial = preferredSourceMaterial ?? font?.material ?? ResolveBeatSaberUiFontMaterial(font);
        if (sourceMaterial == null)
        {
            return null;
        }

        var material = new Material(sourceMaterial)
        {
            name = "BeatRelay_Teko_" + weightName + "_Runtime"
        };

        // Imported mod font materials can carry transparent face colors. Keep the atlas/shader,
        // but make the glyph face opaque so TMP vertex colors control the final row colors.
        if (material.HasProperty("_FaceColor"))
        {
            material.SetColor("_FaceColor", Color.white);
        }

        if (material.HasProperty("_Color"))
        {
            material.SetColor("_Color", Color.white);
        }

        if (material.HasProperty("_ZWrite"))
        {
            material.SetFloat("_ZWrite", 0f);
        }

        if (material.HasProperty("_ZTestMode"))
        {
            material.SetFloat("_ZTestMode", (float)UnityEngine.Rendering.CompareFunction.Always);
        }

        material.color = Color.white;
        material.renderQueue = 4000;
        return material;
    }

    private static Material? ResolveBeatSaberUiFontMaterial(TMP_FontAsset? font)
    {
        try
        {
            var fontAtlas = font?.atlasTexture;
            var materials = Resources.FindObjectsOfTypeAll<Material>();
            if (fontAtlas != null)
            {
                var matchingAtlasMaterial = materials.FirstOrDefault(material =>
                    material != null
                    && material.mainTexture == fontAtlas
                    && material.shader != null
                    && material.shader.name.IndexOf("TextMeshPro", StringComparison.OrdinalIgnoreCase) >= 0
                    && !IsCurvedTextMaterial(material));
                if (matchingAtlasMaterial != null)
                {
                    return matchingAtlasMaterial;
                }
            }

            var tekoMaterial = materials.FirstOrDefault(material =>
                material != null
                && string.Equals(material.name, "Teko-Medium SDF", StringComparison.Ordinal)
                && material.mainTexture != null
                && string.Equals(material.mainTexture.name, "Teko-Medium SDF Atlas", StringComparison.Ordinal));
            if (tekoMaterial != null)
            {
                return tekoMaterial;
            }

            return materials.FirstOrDefault(material =>
                material != null
                && material.mainTexture != null
                && material.shader != null
                && material.shader.name.IndexOf("TextMeshPro", StringComparison.OrdinalIgnoreCase) >= 0
                && !IsCurvedTextMaterial(material)
                && material.name.IndexOf("SDF", StringComparison.OrdinalIgnoreCase) >= 0);
        }
        catch
        {
            return null;
        }
    }

    private static bool IsCurvedTextMaterial(Material? material)
    {
        if (material == null)
        {
            return false;
        }

        var materialName = material.name ?? string.Empty;
        var shaderName = material.shader != null ? material.shader.name : string.Empty;
        return materialName.IndexOf("Curved", StringComparison.OrdinalIgnoreCase) >= 0
            || shaderName.IndexOf("Curved", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private Material? ResolveReadableMaterialForFont(TMP_FontAsset font, Material? preferredMaterial)
    {
        if (preferredMaterial != null && preferredMaterial.mainTexture == font.atlasTexture)
        {
            return preferredMaterial;
        }

        var readableMaterial = CreateReadableFontMaterial(font, "Matched");
        if (readableMaterial != null)
        {
            return readableMaterial;
        }

        return font.material;
    }

    private void LogWorldFontSelectionOnce(TMP_Text line, bool useBold, TMP_FontAsset selectedFont, Material? selectedMaterial)
    {
        if (worldFontSelectionLogged || logger == null)
        {
            return;
        }

        worldFontSelectionLogged = true;
        var selectedMaterialName = selectedMaterial != null ? selectedMaterial.name : "(null)";
        var selectedShaderName = selectedMaterial?.shader != null ? selectedMaterial.shader.name : "(null)";
        var selectedTextureName = selectedMaterial?.mainTexture != null ? selectedMaterial.mainTexture.name : "(null)";
        var fontAtlasName = selectedFont.atlasTexture != null ? selectedFont.atlasTexture.name : "(null)";
        var atlasMismatch = selectedMaterial != null && selectedFont.atlasTexture != null && selectedMaterial.mainTexture != selectedFont.atlasTexture;

        logger.Info(
            "world_overlay_font_probe",
            $"one_shot; line={line.name}; role={(useBold ? "bold" : "medium")}; font={selectedFont.name}; fontAtlas={fontAtlasName}; material={selectedMaterialName}; matShader={selectedShaderName}; matTexture={selectedTextureName}; atlasMismatch={atlasMismatch}; curved={IsCurvedTextMaterial(selectedMaterial)}");
    }

    private void EnsurePreferredFontAvailability()
    {
        if (checkedPreferredFontAvailability)
        {
            return;
        }

        checkedPreferredFontAvailability = true;
        try
        {
            var installedFonts = Font.GetOSInstalledFontNames();
            if (installedFonts == null || installedFonts.Length == 0)
            {
                preferredFontAvailable = false;
                return;
            }

            preferredFontAvailable = installedFonts.Any(IsTekoOsFontName);
        }
        catch
        {
            preferredFontAvailable = false;
        }
    }

    private static bool IsTekoOsFontName(string? name)
    {
        return !string.IsNullOrWhiteSpace(name)
            && name.Trim().StartsWith("Teko", StringComparison.OrdinalIgnoreCase);
    }

    private void LogDesktopFontSelectionOnce(params Font?[] fonts)
    {
        if (desktopFontSelectionLogged || logger == null)
        {
            return;
        }

        desktopFontSelectionLogged = true;
        var parts = fonts
            .Select((font, index) => font == null
                ? $"{index}=(null)"
                : $"{index}={font.name};dynamic={font.dynamic}")
            .ToArray();
        logger.Info("desktop_overlay_font_probe", string.Join("; ", parts));
    }

    private static Font? ResolveFallbackFont()
    {
        try
        {
            var dynamicArial = Font.CreateDynamicFontFromOSFont(new[] { "Arial", "Segoe UI" }, 16);
            if (dynamicArial != null && dynamicArial.dynamic)
            {
                return dynamicArial;
            }
        }
        catch
        {
        }

        try
        {
            var builtin = Resources.GetBuiltinResource<Font>("Arial.ttf");
            return builtin != null && builtin.dynamic ? builtin : null;
        }
        catch
        {
            return null;
        }
    }

    private static IReadOnlyList<DesktopRowValuePart> BuildDesktopRowValueParts(
        OverlayConfig currentConfig,
        OverlayViewModel currentViewModel,
        OverlayRowViewModel row,
        bool expanded)
    {
        var parts = new List<DesktopRowValuePart>();
        if (currentConfig.GetShowModifiers(expanded) && !string.IsNullOrWhiteSpace(row.Modifiers))
        {
            var modifierText = BuildBeatLeaderModifierText(row.Modifiers, includeNoFail: currentViewModel.NoFailPenaltyActive || !row.IsLocalPlayer);
            if (!string.IsNullOrWhiteSpace(modifierText))
            {
                parts.Add(new DesktopRowValuePart(modifierText, new Color32(0x8C, 0x8C, 0x96, 0xFF)));
            }
        }

        if (currentConfig.GetShowAccuracy(expanded) && row.Accuracy.HasValue)
        {
            parts.Add(new DesktopRowValuePart(FormatAccuracy(row.Accuracy.Value), new Color32(0xFF, 0xA4, 0x5A, 0xFF)));
        }

        var showPp = ShouldShowPpValue(currentConfig, currentViewModel, expanded);
        var showScore = ShouldShowScoreValue(currentConfig, currentViewModel, expanded);
        if (showPp && !string.IsNullOrWhiteSpace(row.ValueText))
        {
            parts.Add(new DesktopRowValuePart(row.ValueText, new Color32(0xB7, 0x5C, 0xFF, 0xFF)));
        }

        if (showScore && (expanded || row.IsLocalPlayer))
        {
            var score = row.Score ?? (row.IsLocalPlayer ? currentViewModel.ProjectedScore : null);
            if (score.HasValue)
            {
                parts.Add(new DesktopRowValuePart(score.Value.ToString("N0"), Color.white));
            }
        }

        return parts;
    }

    private static float MeasureDesktopRowValuePartsWidth(
        GUIStyle style,
        IReadOnlyList<DesktopRowValuePart> parts,
        float scale)
    {
        if (parts.Count == 0)
        {
            return 0f;
        }

        var gap = DesktopRowValueGap(scale);
        var width = 0f;
        for (var i = 0; i < parts.Count; i++)
        {
            width += MeasureWidth(style, parts[i].Text);
            if (i > 0)
            {
                width += gap;
            }
        }

        return width;
    }

    private static void DrawDesktopRowValues(
        Rect rect,
        IReadOnlyList<DesktopRowValuePart> parts,
        GUIStyle style,
        float scale)
    {
        if (parts.Count == 0 || rect.width <= 1f)
        {
            return;
        }

        var gap = DesktopRowValueGap(scale);
        var x = rect.xMax;
        var previousColor = style.normal.textColor;
        var previousAlignment = style.alignment;
        style.alignment = TextAnchor.MiddleRight;
        for (var i = parts.Count - 1; i >= 0; i--)
        {
            var part = parts[i];
            var partWidth = Math.Min(MeasureWidth(style, part.Text) + (4f * scale), Math.Max(1f, x - rect.x));
            x -= partWidth;
            if (x < rect.x)
            {
                x = rect.x;
            }

            style.normal.textColor = part.Color;
            DrawDesktopText(new Rect(x, rect.y, partWidth, rect.height), part.Text, style);
            x -= gap;
            if (x <= rect.x)
            {
                break;
            }
        }

        style.normal.textColor = previousColor;
        style.alignment = previousAlignment;
    }

    private static float DesktopRowValueGap(float scale)
    {
        return 0.875f * scale;
    }

    private readonly struct DesktopRowValuePart
    {
        public DesktopRowValuePart(string text, Color color)
        {
            Text = text;
            Color = color;
        }

        public string Text { get; }

        public Color Color { get; }
    }

    private static void DrawDesktopText(Rect rect, string text, GUIStyle? style, bool preferBitmapRenderer = true)
    {
        if (preferBitmapRenderer && style != null && DesktopTekoTextRenderer.TryDraw(rect, text, style))
        {
            return;
        }

        GUI.Label(rect, text, style);
    }

    private sealed class PanelMetrics
    {
        public float Width { get; set; }

        public float Height { get; set; }

        public float Padding { get; set; }

        public float TitleHeight { get; set; }

        public float RankHeight { get; set; }

        public float ModifierHeight { get; set; }

        public float RowHeight { get; set; }

        public float TitleGap { get; set; }

        public float RankGap { get; set; }

        public float RowsTop { get; set; }
    }

    private PanelMetrics MeasurePanelMetrics(float scale, OverlayViewModel viewModel, bool useExpandedSettings = true)
    {
        OverlayConfig currentConfig;
        lock (sync)
        {
            currentConfig = config;
        }
        EnsureStyles(scale, GetBackgroundOpacity01(currentConfig, useExpandedSettings));

        var padding = 14f * scale;
        var titleGap = 0f;
        var showBigRank = currentConfig.GetShowBigRank(useExpandedSettings);
        var bigRankScale = (float)currentConfig.GetBigRankScale(useExpandedSettings);
        var rowScale = (float)currentConfig.GetEffectivePlayerRowScale(useExpandedSettings);
        var showNames = currentConfig.GetShowNames(useExpandedSettings);
        var rankGap = showBigRank ? 8f * scale : 0f;
        var titleHeight = 0f;
        var rankHeight = showBigRank ? Mathf.Max(72f * scale, 72f * scale * bigRankScale) : 0f;
        var modifierHeight = 0f;
        var rowHeight = 28f * scale * rowScale;
        var rowsTop = padding + titleHeight + titleGap + rankHeight + rankGap;
        var contentHeight = rowsTop + (rowHeight * viewModel.Rows.Count) + padding;
        var maxRowWidth = DesktopPanelBaseWidth * scale;
        foreach (var row in viewModel.Rows)
        {
            var rankText = row.Rank.ToString();
            var nameText = showNames
                ? ClipOverlayPlayerName(string.IsNullOrWhiteSpace(row.PlayerName) ? (row.IsLocalPlayer ? viewModel.DisplayName : "Unknown Player") : row.PlayerName)
                : string.Empty;
            if (IsScoreSaberViewModel(viewModel))
            {
                nameText = BuildScoreSaberPlainDetailText(currentConfig, viewModel, row, useExpandedSettings, nameText);
            }

            var valueParts = IsScoreSaberViewModel(viewModel)
                ? Array.Empty<DesktopRowValuePart>()
                : BuildDesktopRowValueParts(currentConfig, viewModel, row, useExpandedSettings);
            var rowStyleForMeasure = row.IsLocalPlayer ? (localRowStyle ?? rowStyle) : rowStyle;
            var rankWidth = Math.Max(32f * scale, MeasureWidth(rowStyleForMeasure!, rankText) + (8f * scale));
            var nameWidth = MeasureWidth(rowStyleForMeasure!, nameText) + (8f * scale);
            var valueWidth = IsScoreSaberViewModel(viewModel)
                ? string.IsNullOrWhiteSpace(FormatScoreSaberScoreText(currentConfig, viewModel, row, useExpandedSettings))
                    ? 0f
                    : Math.Max(112f * scale, MeasureWidth(rowValueStyle!, FormatScoreSaberScoreText(currentConfig, viewModel, row, useExpandedSettings)) + (12f * scale))
                : valueParts.Count == 0 ? 0f : Math.Max(116f * scale, MeasureDesktopRowValuePartsWidth(rowValueStyle!, valueParts, scale) + (12f * scale));
            var rowTotalWidth = (padding * 2f) + rankWidth + nameWidth + valueWidth + (16f * scale);
            if (rowTotalWidth > maxRowWidth)
            {
                maxRowWidth = rowTotalWidth;
            }
        }

        var fixedWidth = Math.Min(Screen.width - (24f * scale), Math.Max(DesktopPanelBaseWidth * scale, maxRowWidth));

        return new PanelMetrics
        {
            Width = fixedWidth,
            Height = Math.Min(Screen.height - (24f * scale), contentHeight),
            Padding = padding,
            TitleHeight = titleHeight,
            RankHeight = rankHeight,
            ModifierHeight = modifierHeight,
            RowHeight = rowHeight,
            TitleGap = titleGap,
            RankGap = rankGap,
            RowsTop = rowsTop
        };
    }

    private static float MeasureWidth(GUIStyle style, string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return 0f;
        }

        return style.CalcSize(new GUIContent(text)).x;
    }

    private static class DesktopTekoTextRenderer
    {
        private const int MaxCachedTextTextures = 96;
        private static readonly Dictionary<string, Texture2D> Cache = new(StringComparer.Ordinal);
        private static readonly Queue<string> CacheOrder = new();
        private static readonly object Sync = new();
        private static System.Drawing.Text.PrivateFontCollection? regularCollection;
        private static System.Drawing.Text.PrivateFontCollection? semiBoldCollection;
        private static System.Drawing.FontFamily? regularFamily;
        private static System.Drawing.FontFamily? semiBoldFamily;
        private static bool attemptedLoad;
        private static readonly Regex ColorTagPattern = new(@"<color=(?<color>#[0-9a-fA-F]{6}(?:[0-9a-fA-F]{2})?)>|</color>", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public static bool TryDraw(Rect rect, string text, GUIStyle style)
        {
            if (string.IsNullOrEmpty(text) || rect.width <= 1f || rect.height <= 1f)
            {
                return true;
            }

            if (!EnsureLoaded())
            {
                return false;
            }

            var width = Mathf.Max(1, Mathf.CeilToInt(rect.width));
            var height = Mathf.Max(1, Mathf.CeilToInt(rect.height));
            var fontSize = Mathf.Max(1, style.fontSize);
            var color = style.normal.textColor;
            var key = string.Join(
                "|",
                text,
                width.ToString(System.Globalization.CultureInfo.InvariantCulture),
                height.ToString(System.Globalization.CultureInfo.InvariantCulture),
                fontSize.ToString(System.Globalization.CultureInfo.InvariantCulture),
                style.fontStyle.ToString(),
                style.alignment.ToString(),
                ColorUtility.ToHtmlStringRGBA(color));

            Texture2D? texture;
            lock (Sync)
            {
                if (!Cache.TryGetValue(key, out texture) || texture == null)
                {
                    texture = CreateTexture(width, height, text, style, color);
                    if (texture == null)
                    {
                        return false;
                    }

                    Cache[key] = texture;
                    CacheOrder.Enqueue(key);
                    TrimCache();
                }
            }

            var previousColor = GUI.color;
            GUI.color = Color.white;
            GUI.DrawTexture(rect, texture, ScaleMode.StretchToFill, alphaBlend: true);
            GUI.color = previousColor;
            return true;
        }

        public static void Clear()
        {
            lock (Sync)
            {
                foreach (var texture in Cache.Values)
                {
                    if (texture != null)
                    {
                        UnityEngine.Object.Destroy(texture);
                    }
                }

                Cache.Clear();
                CacheOrder.Clear();
            }
        }

        private static void TrimCache()
        {
            while (Cache.Count > MaxCachedTextTextures && CacheOrder.Count > 0)
            {
                var oldestKey = CacheOrder.Dequeue();
                if (!Cache.TryGetValue(oldestKey, out var texture))
                {
                    continue;
                }

                Cache.Remove(oldestKey);
                if (texture != null)
                {
                    UnityEngine.Object.Destroy(texture);
                }
            }
        }

        public static bool TryMeasureWidth(string text, GUIStyle style, out float width)
        {
            width = 0f;
            if (string.IsNullOrEmpty(text) || !EnsureLoaded())
            {
                return false;
            }

            try
            {
                using var bitmap = new System.Drawing.Bitmap(1, 1, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                using var graphics = System.Drawing.Graphics.FromImage(bitmap);
                graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                using var font = CreateDrawingFont(style);
                if (TryParseRichColorSegments(text, style.normal.textColor, out var segments))
                {
                    width = MeasureSegmentsWidth(graphics, font, segments) + (GetEdgePadding(style) * 2f);
                }
                else
                {
                    using var format = CreateStringFormat(style);
                    width = graphics.MeasureString(text, font, int.MaxValue, format).Width + (GetEdgePadding(style) * 2f);
                }

                return width > 0f;
            }
            catch
            {
                width = 0f;
                return false;
            }
        }

        private static bool EnsureLoaded()
        {
            if (regularFamily != null && semiBoldFamily != null)
            {
                return true;
            }

            if (attemptedLoad)
            {
                return regularFamily != null || semiBoldFamily != null;
            }

            attemptedLoad = true;
            try
            {
                var regularPath = TekoFontProvider.GetFontFilePath(FontStyle.Normal);
                if (!string.IsNullOrWhiteSpace(regularPath) && System.IO.File.Exists(regularPath))
                {
                    regularCollection = new System.Drawing.Text.PrivateFontCollection();
                    regularCollection.AddFontFile(regularPath);
                    regularFamily = regularCollection.Families.FirstOrDefault();
                }

                var semiBoldPath = TekoFontProvider.GetFontFilePath(FontStyle.Bold);
                if (!string.IsNullOrWhiteSpace(semiBoldPath) && System.IO.File.Exists(semiBoldPath))
                {
                    semiBoldCollection = new System.Drawing.Text.PrivateFontCollection();
                    semiBoldCollection.AddFontFile(semiBoldPath);
                    semiBoldFamily = semiBoldCollection.Families.FirstOrDefault();
                }

                semiBoldFamily ??= regularFamily;
                regularFamily ??= semiBoldFamily;
            }
            catch
            {
            }

            return regularFamily != null || semiBoldFamily != null;
        }

        private static Texture2D? CreateTexture(int width, int height, string text, GUIStyle style, Color color)
        {
            try
            {
                using var bitmap = new System.Drawing.Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
                using (var font = CreateDrawingFont(style))
                {
                    graphics.Clear(System.Drawing.Color.Transparent);
                    graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                    graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
                    graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                    var edgePadding = GetEdgePadding(style);
                    var drawWidth = Math.Max(1f, width - (edgePadding * 2f));
                    if (TryParseRichColorSegments(text, color, out var segments))
                    {
                        DrawRichColorSegments(graphics, font, segments, style, edgePadding, drawWidth, height);
                    }
                    else
                    {
                        using var brush = new System.Drawing.SolidBrush(ToDrawingColor(color));
                        using var format = CreateStringFormat(style);
                        graphics.DrawString(text, font, brush, new System.Drawing.RectangleF(edgePadding, 0f, drawWidth, height), format);
                    }
                }

                var pixels = new Color32[width * height];
                for (var y = 0; y < height; y++)
                {
                    for (var x = 0; x < width; x++)
                    {
                        var pixel = bitmap.GetPixel(x, height - y - 1);
                        pixels[(y * width) + x] = new Color32(pixel.R, pixel.G, pixel.B, pixel.A);
                    }
                }

                var texture = new Texture2D(width, height, TextureFormat.RGBA32, mipChain: false);
                texture.name = "BL_DesktopTekoText";
                texture.wrapMode = TextureWrapMode.Clamp;
                texture.filterMode = FilterMode.Bilinear;
                texture.SetPixels32(pixels);
                texture.Apply(updateMipmaps: false, makeNoLongerReadable: true);
                return texture;
            }
            catch
            {
                return null;
            }
        }

        private static System.Drawing.Font CreateDrawingFont(GUIStyle style)
        {
            var useSemiBold = style.fontStyle is FontStyle.Bold or FontStyle.BoldAndItalic;
            var family = (useSemiBold ? semiBoldFamily : regularFamily) ?? semiBoldFamily ?? regularFamily;
            if (family == null)
            {
                throw new InvalidOperationException("Teko font family was not loaded.");
            }

            var wantsItalic = style.fontStyle is FontStyle.Italic or FontStyle.BoldAndItalic;
            var drawingStyle = wantsItalic && family.IsStyleAvailable(System.Drawing.FontStyle.Italic)
                ? System.Drawing.FontStyle.Italic
                : System.Drawing.FontStyle.Regular;

            return new System.Drawing.Font(family, Mathf.Max(1, style.fontSize), drawingStyle, System.Drawing.GraphicsUnit.Pixel);
        }

        private static bool TryParseRichColorSegments(string text, Color defaultColor, out List<RichColorSegment> segments)
        {
            segments = new List<RichColorSegment>();
            if (string.IsNullOrEmpty(text) || text.IndexOf("<color=", StringComparison.OrdinalIgnoreCase) < 0)
            {
                return false;
            }

            var currentColor = defaultColor;
            var position = 0;
            foreach (Match match in ColorTagPattern.Matches(text))
            {
                if (match.Index > position)
                {
                    segments.Add(new RichColorSegment(text.Substring(position, match.Index - position), currentColor));
                }

                if (match.Value.StartsWith("</color>", StringComparison.OrdinalIgnoreCase))
                {
                    currentColor = defaultColor;
                }
                else
                {
                    var colorText = match.Groups["color"].Value;
                    currentColor = ColorUtility.TryParseHtmlString(colorText, out var parsedColor) ? parsedColor : defaultColor;
                }

                position = match.Index + match.Length;
            }

            if (position < text.Length)
            {
                segments.Add(new RichColorSegment(text.Substring(position), currentColor));
            }

            segments.RemoveAll(segment => string.IsNullOrEmpty(segment.Text));
            return segments.Count > 0;
        }

        private static float MeasureSegmentsWidth(
            System.Drawing.Graphics graphics,
            System.Drawing.Font font,
            IReadOnlyList<RichColorSegment> segments)
        {
            var width = 0f;
            using var format = CreateSegmentStringFormat();
            foreach (var segment in segments)
            {
                width += graphics.MeasureString(segment.Text, font, int.MaxValue, format).Width;
            }

            return width;
        }

        private static void DrawRichColorSegments(
            System.Drawing.Graphics graphics,
            System.Drawing.Font font,
            IReadOnlyList<RichColorSegment> segments,
            GUIStyle style,
            float edgePadding,
            float drawWidth,
            float height)
        {
            var totalWidth = MeasureSegmentsWidth(graphics, font, segments);
            var x = style.alignment switch
            {
                TextAnchor.UpperCenter or TextAnchor.MiddleCenter or TextAnchor.LowerCenter => edgePadding + Math.Max(0f, (drawWidth - totalWidth) * 0.5f),
                TextAnchor.UpperRight or TextAnchor.MiddleRight or TextAnchor.LowerRight => edgePadding + Math.Max(0f, drawWidth - totalWidth),
                _ => edgePadding
            };
            var y = style.alignment switch
            {
                TextAnchor.MiddleLeft or TextAnchor.MiddleCenter or TextAnchor.MiddleRight => Math.Max(0f, (height - font.Height) * 0.5f),
                TextAnchor.LowerLeft or TextAnchor.LowerCenter or TextAnchor.LowerRight => Math.Max(0f, height - font.Height),
                _ => 0f
            };

            using var format = CreateSegmentStringFormat();
            foreach (var segment in segments)
            {
                var segmentWidth = graphics.MeasureString(segment.Text, font, int.MaxValue, format).Width;
                using var brush = new System.Drawing.SolidBrush(ToDrawingColor(segment.Color));
                graphics.DrawString(segment.Text, font, brush, new System.Drawing.RectangleF(x, y, Math.Max(1f, segmentWidth + 2f), height), format);
                x += segmentWidth;
            }
        }

        private static System.Drawing.StringFormat CreateSegmentStringFormat()
        {
            var format = (System.Drawing.StringFormat)System.Drawing.StringFormat.GenericTypographic.Clone();
            format.FormatFlags |= System.Drawing.StringFormatFlags.NoClip;
            format.Trimming = System.Drawing.StringTrimming.None;
            format.Alignment = System.Drawing.StringAlignment.Near;
            format.LineAlignment = System.Drawing.StringAlignment.Near;
            return format;
        }

        private static float GetEdgePadding(GUIStyle style)
        {
            return Mathf.Max(4f, Mathf.Ceil(style.fontSize * 0.16f));
        }

        private static System.Drawing.StringFormat CreateStringFormat(GUIStyle style)
        {
            var format = (System.Drawing.StringFormat)System.Drawing.StringFormat.GenericTypographic.Clone();
            format.FormatFlags |= System.Drawing.StringFormatFlags.NoClip;
            format.Trimming = System.Drawing.StringTrimming.None;
            format.Alignment = style.alignment switch
            {
                TextAnchor.UpperCenter or TextAnchor.MiddleCenter or TextAnchor.LowerCenter => System.Drawing.StringAlignment.Center,
                TextAnchor.UpperRight or TextAnchor.MiddleRight or TextAnchor.LowerRight => System.Drawing.StringAlignment.Far,
                _ => System.Drawing.StringAlignment.Near
            };
            format.LineAlignment = style.alignment switch
            {
                TextAnchor.MiddleLeft or TextAnchor.MiddleCenter or TextAnchor.MiddleRight => System.Drawing.StringAlignment.Center,
                TextAnchor.LowerLeft or TextAnchor.LowerCenter or TextAnchor.LowerRight => System.Drawing.StringAlignment.Far,
                _ => System.Drawing.StringAlignment.Near
            };
            return format;
        }

        private static System.Drawing.Color ToDrawingColor(Color color)
        {
            return System.Drawing.Color.FromArgb(
                Mathf.RoundToInt(Mathf.Clamp01(color.a) * 255f),
                Mathf.RoundToInt(Mathf.Clamp01(color.r) * 255f),
                Mathf.RoundToInt(Mathf.Clamp01(color.g) * 255f),
                Mathf.RoundToInt(Mathf.Clamp01(color.b) * 255f));
        }

        private readonly struct RichColorSegment
        {
            public RichColorSegment(string text, Color color)
            {
                Text = text;
                Color = color;
            }

            public string Text { get; }

            public Color Color { get; }
        }
    }

    private Rect GetPanelRect(string preset, float scale, OverlayViewModel viewModel, bool useExpandedSettings = true)
    {
        var metrics = MeasurePanelMetrics(scale, viewModel, useExpandedSettings);
        var width = metrics.Width;
        var height = metrics.Height;
        var margin = 28f * scale;
        var normalizedPreset = string.IsNullOrWhiteSpace(preset) ? "UpperRight" : preset.Trim();

        if (normalizedPreset.Equals("Center", StringComparison.OrdinalIgnoreCase))
        {
            return new Rect((Screen.width - width) * 0.5f, (Screen.height - height) * 0.5f, width, height);
        }

        var x = normalizedPreset.IndexOf("Left", StringComparison.OrdinalIgnoreCase) >= 0
            ? margin
            : Screen.width - width - margin;
        var y = normalizedPreset.IndexOf("Lower", StringComparison.OrdinalIgnoreCase) >= 0
            ? Screen.height - height - margin
            : margin;

        return new Rect(x, y, width, height);
    }

    private static string StripRichText(string text)
    {
        return string.IsNullOrWhiteSpace(text)
            ? string.Empty
            : Regex.Replace(text, "</?(?:color|size)(?:=[^>]+)?>", string.Empty, RegexOptions.IgnoreCase);
    }

    private static string FormatAccuracy(double accuracy)
    {
        var percent = accuracy <= 1.0001d ? accuracy * 100d : accuracy;
        percent = Math.Max(0d, Math.Min(100d, percent));
        return percent.ToString("0.00") + "%";
    }

    private static string ColorizeRowValue(string color, string text)
    {
        return string.IsNullOrWhiteSpace(text) ? string.Empty : $"<color={color}>{text}</color>";
    }

    private static bool IsScoreSaberViewModel(OverlayViewModel viewModel)
    {
        return string.Equals(viewModel.SourceName, "ScoreSaber", StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildScoreSaberDesktopDetailText(
        OverlayConfig currentConfig,
        OverlayViewModel currentViewModel,
        OverlayRowViewModel row,
        bool expanded,
        string playerName)
    {
        var baseColor = row.IsLocalPlayer ? "#00C8FF" : null;
        return BuildScoreSaberDetailText(currentConfig, currentViewModel, row, expanded, playerName, smallPp: false, separator: ScoreSaberDesktopDetailSeparator, baseColor: baseColor);
    }

    private static string BuildScoreSaberPlainDetailText(
        OverlayConfig currentConfig,
        OverlayViewModel currentViewModel,
        OverlayRowViewModel row,
        bool expanded,
        string playerName)
    {
        return StripRichText(BuildScoreSaberDetailText(currentConfig, currentViewModel, row, expanded, playerName, smallPp: false, separator: "   "));
    }

    private static string BuildScoreSaberDetailText(
        OverlayConfig currentConfig,
        OverlayViewModel currentViewModel,
        OverlayRowViewModel row,
        bool expanded,
        string playerName,
        bool smallPp,
        string separator,
        string? baseColor = null)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(playerName))
        {
            parts.Add(ColorizeRichText(playerName, baseColor));
        }

        if (currentConfig.GetShowAccuracy(expanded) && row.Accuracy.HasValue)
        {
            parts.Add($"{ColorizeRichText("(", baseColor)}<color=#FFD52A>{FormatAccuracy(row.Accuracy.Value)}</color>{ColorizeRichText(")", baseColor)}");
        }

        if (ShouldShowPpValue(currentConfig, currentViewModel, expanded) && !string.IsNullOrWhiteSpace(row.ValueText))
        {
            parts.Add($"{ColorizeRichText("(", baseColor)}<color=#6872E5>{FormatScoreSaberPp(row.ValueText, smallPp)}</color>{ColorizeRichText(")", baseColor)}");
        }

        if (currentConfig.GetShowModifiers(expanded))
        {
            foreach (var modifier in NormalizeScoreSaberModifiers(row.Modifiers, includeNoFail: true))
            {
                parts.Add($"<color=#4E4E56>[{modifier}]</color>");
            }
        }

        return string.Join(separator, parts);
    }

    private static string ColorizeRichText(string text, string? color)
    {
        return string.IsNullOrWhiteSpace(color) ? text : $"<color={color}>{text}</color>";
    }

    private static string FormatScoreSaberPp(string valueText, bool smallPp)
    {
        if (string.IsNullOrWhiteSpace(valueText))
        {
            return string.Empty;
        }

        var trimmed = valueText.Trim();
        if (!trimmed.EndsWith("pp", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed;
        }

        var number = trimmed.Substring(0, trimmed.Length - 2).Trim();
        return smallPp
            ? $"{number}<size=60%>pp</size>"
            : number + "pp";
    }

    private static string FormatScoreSaberScoreText(OverlayConfig currentConfig, OverlayViewModel currentViewModel, OverlayRowViewModel row, bool expanded)
    {
        if (!ShouldShowScoreValue(currentConfig, currentViewModel, expanded))
        {
            return string.Empty;
        }

        if (!expanded && !row.IsLocalPlayer)
        {
            return string.Empty;
        }

        var score = row.Score ?? (row.IsLocalPlayer ? currentViewModel.ProjectedScore : null);
        return score.HasValue && score.Value > 0
            ? score.Value.ToString("#,0", System.Globalization.CultureInfo.InvariantCulture)
            : string.Empty;
    }

    private static string BuildBeatLeaderModifierText(string modifiers, bool includeNoFail)
    {
        var displayModifiers = BeatLeaderModifierPolicy.GetDisplayModifiers(SplitModifierText(modifiers), includeNoFail);
        return displayModifiers.Count == 0
            ? string.Empty
            : string.Join(",", displayModifiers);
    }

    private static IReadOnlyList<string> SplitModifierText(string modifiers)
    {
        if (string.IsNullOrWhiteSpace(modifiers))
        {
            return Array.Empty<string>();
        }

        return modifiers
            .Split(new[] { ',', ';', ' ', '+' }, StringSplitOptions.RemoveEmptyEntries)
            .ToList();
    }

    private static IReadOnlyList<string> NormalizeScoreSaberModifiers(string modifiers, bool includeNoFail)
    {
        if (string.IsNullOrWhiteSpace(modifiers))
        {
            return Array.Empty<string>();
        }

        var tokens = modifiers
            .Split(new[] { ',', ';', ' ', '+' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(NormalizeScoreSaberModifierToken)
            .Where(modifier => !string.IsNullOrWhiteSpace(modifier))
            .Where(modifier => !string.Equals(modifier, "ZM", StringComparison.OrdinalIgnoreCase))
            .Where(modifier => includeNoFail || !string.Equals(modifier, "NF", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return tokens.Count == 0 ? Array.Empty<string>() : tokens!;
    }

    private static string NormalizeScoreSaberModifierToken(string modifier)
    {
        var token = modifier.Trim().ToUpperInvariant().Replace(" ", string.Empty).Replace("-", string.Empty).Replace("_", string.Empty);
        return token switch
        {
            "NOFAIL" or "NOFAILON0ENERGY" => "NF",
            "SLOWER" or "SLOWERSONG" => "SS",
            "NOBOMBS" => "NB",
            "NOWALLS" or "NOOBSTACLES" => "NO",
            "NOARROWS" or "NONOTES" => "NA",
            "DISAPPEARINGARROWS" => "DA",
            "FASTER" or "FASTERSONG" => "FS",
            "SUPERFAST" or "SUPERFASTSONG" or "SFS" => "SF",
            "GHOSTNOTES" => "GN",
            "PRO" or "PROMODE" => "PM",
            "SMALLNOTES" or "SMALLCUBES" => "SC",
            "STRICTANGLES" => "SA",
            "ZEN" or "ZENMODE" => "ZM",
            _ => token
        };
    }

    private static bool ShouldShowPpValue(OverlayConfig currentConfig, OverlayViewModel currentViewModel, bool expanded)
    {
        return currentViewModel.SupportsPp
            && currentConfig.GetShowPp(expanded)
            && !IsBeatLeaderUnrankedViewModel(currentViewModel);
    }

    private static bool ShouldShowScoreValue(OverlayConfig currentConfig, OverlayViewModel currentViewModel, bool expanded)
    {
        return currentConfig.DynamicPpScore
            && currentConfig.GetShowPp(expanded)
            && (!currentViewModel.SupportsPp || IsBeatLeaderUnrankedViewModel(currentViewModel))
            || currentConfig.GetShowScore(expanded);
    }

    private static bool IsBeatLeaderUnrankedViewModel(OverlayViewModel currentViewModel)
    {
        return !currentViewModel.IsRanked
            && !IsScoreSaberViewModel(currentViewModel);
    }

    private static Texture2D MakeTexture(Color color)
    {
        var texture = new Texture2D(1, 1);
        texture.SetPixel(0, 0, color);
        texture.Apply();
        return texture;
    }

    private static float GetBackgroundOpacity01(OverlayConfig currentConfig, bool expanded)
    {
        return Mathf.Clamp01((float)(currentConfig.GetBackgroundOpacity(expanded) / 100.0));
    }

    private static void ApplyWorldQuadColor(GameObject quad, Color color)
    {
        var renderer = quad.GetComponent<MeshRenderer>();
        var material = renderer != null ? renderer.sharedMaterial : null;
        if (material != null && material.color != color)
        {
            material.color = color;
        }
    }

    private static GameObject CreateWorldQuad(string name, Transform parent, Texture2D? texture, Color color, int sortingOrder)
    {
        var quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
        quad.name = name;
        quad.transform.SetParent(parent, false);
        var renderer = quad.GetComponent<MeshRenderer>();
        renderer.sharedMaterial = CreateWorldQuadMaterial(texture, color);
        renderer.sortingOrder = sortingOrder;
        return quad;
    }

    private static Material CreateWorldQuadMaterial(Texture2D? texture, Color color)
    {
        var shader = Shader.Find("Unlit/Transparent")
            ?? Shader.Find("Sprites/Default")
            ?? Shader.Find("Legacy Shaders/Transparent/Diffuse")
            ?? Shader.Find("Standard");
        if (shader == null)
        {
            throw new InvalidOperationException("Could not resolve a Unity shader for the overlay quad.");
        }

        var material = new Material(shader);
        material.color = color;
        if (texture != null)
        {
            material.mainTexture = texture;
        }

        if (material.HasProperty("_ZWrite"))
        {
            material.SetFloat("_ZWrite", 0f);
        }

        material.renderQueue = 3000;
        return material;
    }

    private static string ValueOrFallback(string preferred, string fallback)
    {
        if (!string.IsNullOrWhiteSpace(preferred))
        {
            return preferred;
        }

        return string.IsNullOrWhiteSpace(fallback) ? "--" : fallback;
    }

    private void UpdateRankAnimation()
    {
        expansionBlend = (float)expansionTransition.Advance(
            targetExpansionBlend,
            Time.unscaledDeltaTime);

        if (hasAnimatedRankValue)
        {
            animatedRankValue = Mathf.Lerp((float)animatedRankValue, targetRankValue, Time.unscaledDeltaTime * 9f);
            if (Math.Abs(animatedRankValue - targetRankValue) <= 0.05d)
            {
                animatedRankValue = targetRankValue;
            }

            rankSlideOffsetY = Mathf.Lerp(rankSlideOffsetY, 0f, Time.unscaledDeltaTime * 10f);
            if (Mathf.Abs(rankSlideOffsetY) <= 0.05f)
            {
                rankSlideOffsetY = 0f;
            }

            rankFlashStrength = Mathf.MoveTowards(rankFlashStrength, 0f, Time.unscaledDeltaTime * 1.75f);
        }

        var keys = rowAnimationOffsets.Keys.ToList();
        foreach (var key in keys)
        {
            var offset = Mathf.Lerp(rowAnimationOffsets[key], 0f, Time.unscaledDeltaTime * 11f);
            if (Mathf.Abs(offset) <= 0.2f)
            {
                rowAnimationOffsets.Remove(key);
                continue;
            }

            rowAnimationOffsets[key] = offset;
        }
    }

    private void UpdateRowOrderAnimations(IReadOnlyList<OverlayRowViewModel> previousRows, IReadOnlyList<OverlayRowViewModel> nextRows)
    {
        if (previousRows.Count == 0 || nextRows.Count == 0)
        {
            return;
        }

        var previousIndices = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < previousRows.Count; i++)
        {
            previousIndices[BuildRowKey(previousRows[i])] = i;
        }

        for (var i = 0; i < nextRows.Count; i++)
        {
            var key = BuildRowKey(nextRows[i]);
            if (!previousIndices.TryGetValue(key, out var previousIndex))
            {
                continue;
            }

            var indexOffset = previousIndex - i;
            if (indexOffset != 0)
            {
                rowAnimationOffsets[key] = indexOffset * 22f;
                continue;
            }

            var previousRow = previousRows[previousIndex];
            if (previousRow.Rank != nextRows[i].Rank)
            {
                rowAnimationOffsets[key] = Math.Sign(previousRow.Rank - nextRows[i].Rank) * 22f;
            }
        }
    }

    private bool HasWorldAnimationInProgress()
    {
        return OverlayExpansionTransition.IsInProgress(expansionBlend, targetExpansionBlend);
    }

    private float GetEasedExpansionBlend()
    {
        return expansionBlend;
    }

    private static string BuildRowKey(OverlayRowViewModel row)
    {
        if (!string.IsNullOrWhiteSpace(row.PlayerId))
        {
            return row.PlayerId.Trim().ToLowerInvariant();
        }

        return row.PlayerName.Trim().ToLowerInvariant();
    }

    private static string BuildViewModelSignature(OverlayViewModel viewModel, OverlayConfig currentConfig)
    {
        if (viewModel == null)
        {
            return string.Empty;
        }

        var expanded = viewModel.Mode == OverlayMode.Expanded;
        var showAccuracy = currentConfig.GetShowAccuracy(expanded);
        var showPp = ShouldShowPpValue(currentConfig, viewModel, expanded);
        var showScore = ShouldShowScoreValue(currentConfig, viewModel, expanded);
        var builder = new StringBuilder(256);
        builder.Append((int)viewModel.Mode).Append('|')
            .Append(currentConfig.GetShowNames(expanded)).Append('|')
            .Append(showAccuracy).Append('|')
            .Append(showPp).Append('|')
            .Append(showScore).Append('|')
            .Append(currentConfig.GetShowModifiers(expanded)).Append('|')
            .Append(currentConfig.GetShowHighlight(expanded)).Append('|')
            .Append(currentConfig.GetShowBigRank(expanded)).Append('|')
            .Append(currentConfig.GetBigRankScale(expanded).ToString("R", System.Globalization.CultureInfo.InvariantCulture)).Append('|')
            .Append(currentConfig.GetPlayerRowScale(expanded).ToString("R", System.Globalization.CultureInfo.InvariantCulture)).Append('|')
            .Append(currentConfig.GetBackgroundOpacity(expanded).ToString("R", System.Globalization.CultureInfo.InvariantCulture)).Append('|')
            .Append(viewModel.DisplayName).Append('|')
            .Append(viewModel.RankText).Append('|')
            .Append(viewModel.MovementText).Append('|')
            .Append(viewModel.MessageText).Append('|')
            .Append(viewModel.MapContextText).Append('|')
            .Append(viewModel.ModifiersText).Append('|')
            .Append(viewModel.IsRanked).Append('|')
            .Append(viewModel.SupportsPp).Append('|')
            .Append(viewModel.SourceName).Append('|')
            .Append(showScore ? viewModel.ProjectedScore?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty : string.Empty).Append('|')
            .Append(viewModel.AnimateRankChange).Append('|')
            .Append(viewModel.NoFailPenaltyActive);

        foreach (var row in viewModel.Rows)
        {
            builder.Append("||")
                .Append(row.Rank).Append('|')
                .Append(row.PlayerId).Append('|')
                .Append(row.PlayerName).Append('|')
                .Append(showPp ? row.ValueText : string.Empty).Append('|')
                .Append(row.Modifiers).Append('|')
                .Append(showAccuracy ? row.Accuracy?.ToString("R", System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty : string.Empty).Append('|')
                .Append(showScore ? row.Score?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty : string.Empty).Append('|')
                .Append(row.IsLocalPlayer).Append('|')
                .Append(row.IsProjected);
        }

        return builder.ToString();
    }

    private static bool TryParseNumericRank(string rankText, out int rank)
    {
        rank = 0;
        if (string.IsNullOrWhiteSpace(rankText) || !rankText.StartsWith("#", StringComparison.Ordinal))
        {
            return false;
        }

        return int.TryParse(rankText.Substring(1), out rank) && rank > 0;
    }

    private string GetDisplayedRankText(OverlayViewModel currentViewModel)
    {
        if (TryResolveLiveRank(currentViewModel, out var resolvedRank))
        {
            if (hasAnimatedRankValue)
            {
                return "#" + Math.Max(1, (int)Math.Round(animatedRankValue, MidpointRounding.AwayFromZero)).ToString();
            }

            return "#" + resolvedRank.ToString();
        }

        if (hasAnimatedRankValue)
        {
            return "#" + Math.Max(1, (int)Math.Round(animatedRankValue, MidpointRounding.AwayFromZero)).ToString();
        }

        if (TryParseNumericRank(currentViewModel.RankText, out var parsed))
        {
            return "#" + parsed.ToString();
        }

        return ValueOrFallback(currentViewModel.RankText, currentViewModel.MessageText);
    }

    private string GetDisplayedWorldRankText(OverlayViewModel currentViewModel)
    {
        if (TryResolveLiveRank(currentViewModel, out var resolvedRank))
        {
            return "#" + resolvedRank.ToString();
        }

        if (TryParseNumericRank(currentViewModel.RankText, out var parsed))
        {
            return "#" + parsed.ToString();
        }

        return ValueOrFallback(currentViewModel.RankText, currentViewModel.MessageText);
    }

    private static bool TryResolveLiveRank(OverlayViewModel currentViewModel, out int rank)
    {
        rank = 0;
        var localRow = currentViewModel.Rows.FirstOrDefault(row => row.IsLocalPlayer && row.Rank > 0);
        if (localRow != null)
        {
            rank = localRow.Rank;
            return true;
        }

        return TryParseNumericRank(currentViewModel.RankText, out rank);
    }

    private Color GetRankColor()
    {
        if (rankFlashStrength <= 0f)
        {
            return Color.white;
        }

        return Color.Lerp(Color.white, rankFlashColor, rankFlashStrength);
    }

    private static Color GetValueTextColor(bool isRanked)
    {
        return isRanked
            ? new Color32(0xA0, 0x50, 0xF0, 0xFF)
            : Color.white;
    }

    private static Color GetRowValueTextColor(OverlayConfig currentConfig, OverlayViewModel currentViewModel, OverlayRowViewModel row, bool expanded)
    {
        if (ShouldShowScoreValue(currentConfig, currentViewModel, expanded)
            && row.IsLocalPlayer
            && currentViewModel.ProjectedScore.HasValue)
        {
            return Color.white;
        }

        if (currentConfig.GetShowAccuracy(expanded) && row.Accuracy.HasValue)
        {
            return new Color32(0xFF, 0xB3, 0x2E, 0xFF);
        }

        return GetValueTextColor(currentViewModel.IsRanked);
    }

}
#endif
