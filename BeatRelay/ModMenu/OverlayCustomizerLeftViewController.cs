#if NETFRAMEWORK
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using BeatRelay.Config;
using BeatSaberMarkupLanguage.Attributes;
using BeatSaberMarkupLanguage.ViewControllers;
using HMUI;
using UnityEngine;
using UnityEngine.UI;

namespace BeatRelay.ModMenu;

[ViewDefinition("BeatRelay.Resources.BSML.OverlayCustomizerLeft.bsml")]
internal sealed class OverlayCustomizerLeftViewController : BSMLAutomaticViewController, INotifyPropertyChanged
{
    private Plugin? plugin;
    private OverlayCustomizerPreviewContext? previewContext;
    private string selectedFilter = "General";

    private static Sprite? beatLeaderGradientSprite;
    private static Sprite? scoreSaberGradientSprite;

    [UIComponent("BeatLeaderButton")]
    private NoTransitionsButton? beatLeaderButton;

    [UIComponent("ScoreSaberButton")]
    private NoTransitionsButton? scoreSaberButton;

    public event PropertyChangedEventHandler? PropertyChanged;

    public void Initialize(Plugin nextPlugin, OverlayCustomizerPreviewContext nextPreviewContext)
    {
        plugin = nextPlugin;
        previewContext = nextPreviewContext;
        ApplyLeaderboardButtonGradients();
        NotifyAll();
    }

    protected override void DidActivate(bool firstActivation, bool addedToHierarchy, bool screenSystemEnabling)
    {
        base.DidActivate(firstActivation, addedToHierarchy, screenSystemEnabling);
        ApplyLeaderboardButtonGradients();
    }

    [UIValue("LeaderboardSource")]
    public string LeaderboardSource
    {
        get => Config.LeaderboardSource;
        set => ApplyConfig(config => config.LeaderboardSource = string.IsNullOrWhiteSpace(value) ? "BeatLeader" : value);
    }

    [UIValue("LeaderboardSources")]
    public IReadOnlyList<string> LeaderboardSources { get; } = new[] { "BeatLeader", "ScoreSaber" };

    [UIValue("BeatLeaderModeLabel")]
    public string BeatLeaderModeLabel => "BEATLEADER";

    [UIValue("ScoreSaberModeLabel")]
    public string ScoreSaberModeLabel => "SCORESABER";

    [UIValue("BeatLeaderLogo")]
    public string BeatLeaderLogo => "BeatRelay.Resources.Images.BeatLeader.png";

    [UIValue("ScoreSaberLogo")]
    public string ScoreSaberLogo => "BeatRelay.Resources.Images.ScoreSaber.png";

    [UIValue("BeatLeaderSelected")]
    public bool BeatLeaderSelected => Config.LeaderboardSource.Equals("BeatLeader", StringComparison.OrdinalIgnoreCase);

    [UIValue("ScoreSaberSelected")]
    public bool ScoreSaberSelected => Config.LeaderboardSource.Equals("ScoreSaber", StringComparison.OrdinalIgnoreCase);

    [UIValue("BeatLeaderSettingsVisible")]
    public bool BeatLeaderSettingsVisible => BeatLeaderSelected;

    [UIValue("BeatLeaderModeColor")]
    public string BeatLeaderModeColor => Config.LeaderboardSource.Equals("BeatLeader", StringComparison.OrdinalIgnoreCase)
        ? "#F150CF"
        : "#6E5870";

    [UIValue("ScoreSaberModeColor")]
    public string ScoreSaberModeColor => Config.LeaderboardSource.Equals("ScoreSaber", StringComparison.OrdinalIgnoreCase)
        ? "#FFE246"
        : "#7A6E45";

    [UIValue("SelectedFilter")]
    public string SelectedFilter
    {
        get => selectedFilter;
        set
        {
            selectedFilter = NormalizeFilter(value);
            NotifyAll();
        }
    }

    [UIValue("SettingsFilters")]
    public IReadOnlyList<string> SettingsFilters { get; } = new[] { "General", "Behavior" };

    [UIValue("GeneralTabActive")]
    public bool GeneralTabActive => selectedFilter.Equals("General", StringComparison.OrdinalIgnoreCase);

    [UIValue("GeneralTabInactive")]
    public bool GeneralTabInactive => !GeneralTabActive;

    [UIValue("BehaviorTabActive")]
    public bool BehaviorTabActive => selectedFilter.Equals("Behavior", StringComparison.OrdinalIgnoreCase);

    [UIValue("BehaviorTabInactive")]
    public bool BehaviorTabInactive => !BehaviorTabActive;

    [UIValue("GeneralTabColor")]
    public string GeneralTabColor => GeneralTabActive ? "#159BE8" : "#141827";

    [UIValue("BehaviorTabColor")]
    public string BehaviorTabColor => BehaviorTabActive ? "#159BE8" : "#141827";

    [UIValue("PreviewMode")]
    public string PreviewMode
    {
        get => Config.PreviewExpanded ? "Expanded" : "Collapsed";
        set
        {
            if (plugin == null)
            {
                return;
            }

            var nextExpanded = string.Equals(value, "Expanded", StringComparison.OrdinalIgnoreCase);
            if (plugin.Config.PreviewExpanded == nextExpanded)
            {
                NotifyGeneralSettings();
                return;
            }

            plugin.Config.PreviewExpanded = nextExpanded;
            CommitAndRefresh();
            NotifyGeneralSettings();
        }
    }

    [UIValue("PreviewModes")]
    public IReadOnlyList<string> PreviewModes { get; } = new[] { "Collapsed", "Expanded" };

    [UIValue("CollapsedToggleVisible")]
    public bool CollapsedToggleVisible => !Config.AlwaysExpand;

    [UIValue("PreviewModeVisible")]
    public bool PreviewModeVisible => GeneralTabActive;

    [UIValue("CollapsedPreviewSelected")]
    public bool CollapsedPreviewSelected => !Config.PreviewExpanded;

    [UIValue("ExpandedPreviewSelected")]
    public bool ExpandedPreviewSelected => Config.PreviewExpanded;

    [UIValue("CollapsedPreviewUnselected")]
    public bool CollapsedPreviewUnselected => Config.PreviewExpanded;

    [UIValue("ExpandedPreviewUnselected")]
    public bool ExpandedPreviewUnselected => !Config.PreviewExpanded;

    [UIValue("CollapsedPreviewColor")]
    public string CollapsedPreviewColor => Config.AlwaysExpand ? "#4B4F5A" : Config.PreviewExpanded ? "#141827" : "#159BE8";

    [UIValue("ExpandedPreviewColor")]
    public string ExpandedPreviewColor => Config.PreviewExpanded ? "#159BE8" : "#141827";

    [UIValue("CollapsedPreviewHoverHint")]
    public string CollapsedPreviewHoverHint => Config.AlwaysExpand ? "Always expand is enabled." : string.Empty;

    [UIValue("CollapsedShowBigRank")]
    public bool CollapsedShowBigRank
    {
        get => Config.GetShowBigRank(expanded: false);
        set => ApplyConfig(config => config.SetShowBigRank(expanded: false, value));
    }

    [UIValue("CollapsedBigRankScale")]
    public float CollapsedBigRankScale
    {
        get => (float)Config.GetBigRankScale(expanded: false);
        set => ApplyConfig(config => config.SetBigRankScale(expanded: false, value));
    }

    [UIValue("CollapsedPlayerRowScale")]
    public float CollapsedPlayerRowScale
    {
        get => (float)Config.GetPlayerRowScale(expanded: false);
        set => ApplyConfig(config => config.SetPlayerRowScale(expanded: false, value));
    }

    [UIValue("CollapsedBackgroundOpacity")]
    public float CollapsedBackgroundOpacity
    {
        get => (float)Config.GetBackgroundOpacity(expanded: false);
        set => ApplyConfig(config => config.SetBackgroundOpacity(expanded: false, value));
    }

    [UIValue("CollapsedShowHighlight")]
    public bool CollapsedShowHighlight
    {
        get => Config.GetShowHighlight(expanded: false);
        set => ApplyConfig(config => config.SetShowHighlight(expanded: false, value));
    }

    [UIValue("CollapsedShowPp")]
    public bool CollapsedShowPp
    {
        get => Config.GetShowPp(expanded: false);
        set => ApplyConfig(config => SetShowPpAndUpdateDynamic(config, expanded: false, value));
    }

    [UIValue("CollapsedShowModifiers")]
    public bool CollapsedShowModifiers
    {
        get => Config.GetShowModifiers(expanded: false);
        set => ApplyConfig(config => config.SetShowModifiers(expanded: false, value));
    }

    [UIValue("CollapsedShowNames")]
    public bool CollapsedShowNames
    {
        get => Config.GetShowNames(expanded: false);
        set => ApplyConfig(config => config.SetShowNames(expanded: false, value));
    }

    [UIValue("CollapsedShowAccuracy")]
    public bool CollapsedShowAccuracy
    {
        get => Config.GetShowAccuracy(expanded: false);
        set => ApplyConfig(config => config.SetShowAccuracy(expanded: false, value));
    }

    [UIValue("CollapsedShowScore")]
    public bool CollapsedShowScore
    {
        get => Config.GetShowScore(expanded: false);
        set => ApplyConfig(config => config.SetShowScore(expanded: false, value));
    }

    [UIValue("ExpandedShowBigRank")]
    public bool ExpandedShowBigRank
    {
        get => Config.GetShowBigRank(expanded: true);
        set => ApplyConfig(config => config.SetShowBigRank(expanded: true, value));
    }

    [UIValue("ExpandedBigRankScale")]
    public float ExpandedBigRankScale
    {
        get => (float)Config.GetBigRankScale(expanded: true);
        set => ApplyConfig(config => config.SetBigRankScale(expanded: true, value));
    }

    [UIValue("ExpandedPlayerRowScale")]
    public float ExpandedPlayerRowScale
    {
        get => (float)Config.GetPlayerRowScale(expanded: true);
        set => ApplyConfig(config => config.SetPlayerRowScale(expanded: true, value));
    }

    [UIValue("ExpandedBackgroundOpacity")]
    public float ExpandedBackgroundOpacity
    {
        get => (float)Config.GetBackgroundOpacity(expanded: true);
        set => ApplyConfig(config => config.SetBackgroundOpacity(expanded: true, value));
    }

    [UIValue("ExpandedShowHighlight")]
    public bool ExpandedShowHighlight
    {
        get => Config.GetShowHighlight(expanded: true);
        set => ApplyConfig(config => config.SetShowHighlight(expanded: true, value));
    }

    [UIValue("ExpandedShowPp")]
    public bool ExpandedShowPp
    {
        get => Config.GetShowPp(expanded: true);
        set => ApplyConfig(config => SetShowPpAndUpdateDynamic(config, expanded: true, value));
    }

    [UIValue("ExpandedShowModifiers")]
    public bool ExpandedShowModifiers
    {
        get => Config.GetShowModifiers(expanded: true);
        set => ApplyConfig(config => config.SetShowModifiers(expanded: true, value));
    }

    [UIValue("ExpandedShowNames")]
    public bool ExpandedShowNames
    {
        get => Config.GetShowNames(expanded: true);
        set => ApplyConfig(config => config.SetShowNames(expanded: true, value));
    }

    [UIValue("ExpandedShowAccuracy")]
    public bool ExpandedShowAccuracy
    {
        get => Config.GetShowAccuracy(expanded: true);
        set => ApplyConfig(config => config.SetShowAccuracy(expanded: true, value));
    }

    [UIValue("ExpandedShowScore")]
    public bool ExpandedShowScore
    {
        get => Config.GetShowScore(expanded: true);
        set => ApplyConfig(config => config.SetShowScore(expanded: true, value));
    }

    [UIValue("ShowBigRank")]
    public bool ShowBigRank
    {
        get => Config.GetShowBigRank(Config.PreviewExpanded);
        set => ApplyConfig(config => config.SetShowBigRank(config.PreviewExpanded, value));
    }

    [UIValue("BigRankScale")]
    public float BigRankScale
    {
        get => (float)Config.GetBigRankScale(Config.PreviewExpanded);
        set => ApplyConfig(config => config.SetBigRankScale(config.PreviewExpanded, value));
    }

    [UIValue("PlayerRowScale")]
    public float PlayerRowScale
    {
        get => (float)Config.GetPlayerRowScale(Config.PreviewExpanded);
        set => ApplyConfig(config => config.SetPlayerRowScale(config.PreviewExpanded, value));
    }

    [UIValue("BackgroundOpacity")]
    public float BackgroundOpacity
    {
        get => (float)Config.GetBackgroundOpacity(Config.PreviewExpanded);
        set => ApplyConfig(config => config.SetBackgroundOpacity(config.PreviewExpanded, value));
    }

    [UIValue("ShowHighlight")]
    public bool ShowHighlight
    {
        get => Config.GetShowHighlight(Config.PreviewExpanded);
        set => ApplyConfig(config => config.SetShowHighlight(config.PreviewExpanded, value));
    }

    [UIValue("ShowPp")]
    public bool ShowPp
    {
        get => Config.GetShowPp(Config.PreviewExpanded);
        set => ApplyConfig(config => config.SetShowPp(config.PreviewExpanded, value));
    }

    [UIValue("ShowModifiers")]
    public bool ShowModifiers
    {
        get => Config.GetShowModifiers(Config.PreviewExpanded);
        set => ApplyConfig(config => config.SetShowModifiers(config.PreviewExpanded, value));
    }

    [UIValue("ShowNames")]
    public bool ShowNames
    {
        get => Config.GetShowNames(Config.PreviewExpanded);
        set => ApplyConfig(config => config.SetShowNames(config.PreviewExpanded, value));
    }

    [UIValue("ShowAccuracy")]
    public bool ShowAccuracy
    {
        get => Config.GetShowAccuracy(Config.PreviewExpanded);
        set => ApplyConfig(config => config.SetShowAccuracy(config.PreviewExpanded, value));
    }

    [UIValue("ShowScore")]
    public bool ShowScore
    {
        get => Config.GetShowScore(Config.PreviewExpanded);
        set => ApplyConfig(config => config.SetShowScore(config.PreviewExpanded, value));
    }

    [UIValue("Enabled")]
    public bool Enabled
    {
        get => Config.Enabled;
        set => ApplyConfig(config => config.Enabled = value);
    }

    [UIValue("EnableDesktopOverlay")]
    public bool EnableDesktopOverlay
    {
        get => Config.EnableDesktopOverlay;
        set => ApplyConfig(config => config.EnableDesktopOverlay = value);
    }

    [UIValue("DynamicPpScore")]
    public bool DynamicPpScore
    {
        get => Config.DynamicPpScore;
        set => ApplyConfig(config => config.DynamicPpScore = value && CountAvailableShowPpOptions(config) > 0);
    }

    [UIValue("DynamicPpScoreInteractable")]
    public bool DynamicPpScoreInteractable => CountAvailableShowPpOptions(Config) > 0;

    [UIValue("DynamicPpScoreHoverHint")]
    public string DynamicPpScoreHoverHint => DynamicPpScoreInteractable
        ? "Uses pp in ranked, Uses score in unranked."
        : "Enable Show PP in collapsed or expanded to use dynamic pp/score.";

    [UIValue("PositionPreset")]
    public string PositionPreset
    {
        get => Config.PositionPreset;
        set => ApplyConfig(config => config.PositionPreset = string.IsNullOrWhiteSpace(value) ? "AboveMultiplier" : value);
    }

    [UIValue("PositionPresets")]
    public IReadOnlyList<string> PositionPresets { get; } = new[]
    {
        "BelowCombo",
        "AboveCombo",
        "BelowMultiplier",
        "AboveMultiplier",
        "BelowEnergy",
        "AboveHighway"
    };

    [UIAction("FormatPositionPreset")]
    public string FormatPositionPreset(object value)
    {
        var text = value?.ToString() ?? string.Empty;
        return text switch
        {
            "BelowCombo" => "Below Combo",
            "AboveCombo" => "Above Combo",
            "BelowMultiplier" => "Below Multiplier",
            "AboveMultiplier" => "Above Multiplier",
            "BelowEnergy" => "Below Energy",
            "AboveHighway" => "Over Highway",
            _ => text
        };
    }

    [UIValue("InGameOverlayScale")]
    public float InGameOverlayScale
    {
        get => (float)Config.InGameOverlayScale;
        set => ApplyConfig(config => config.InGameOverlayScale = value);
    }

    [UIValue("DesktopOverlayScale")]
    public float DesktopOverlayScale
    {
        get => (float)Config.DesktopOverlayScale;
        set => ApplyConfig(config => config.DesktopOverlayScale = value);
    }

    [UIValue("UpdateIntervalSeconds")]
    public float UpdateIntervalSeconds
    {
        get => (float)Config.UpdateIntervalSeconds;
        set => ApplyConfig(config => config.UpdateIntervalSeconds = value);
    }

    [UIValue("AlwaysExpand")]
    public bool AlwaysExpand
    {
        get => Config.AlwaysExpand;
        set => ApplyConfig(config =>
        {
            config.AlwaysExpand = value;
            if (value)
            {
                config.PreviewExpanded = true;
            }

            if (CountAvailableShowPpOptions(config) == 0)
            {
                config.DynamicPpScore = false;
            }
        });
    }

    [UIValue("ExpandOnBreakInteractable")]
    public bool ExpandOnBreakInteractable => !Config.AlwaysExpand;

    [UIValue("ExpandOnBreakSeconds")]
    public float ExpandOnBreakSeconds
    {
        get => (float)Config.ExpandOnBreakSeconds;
        set => ApplyConfig(config => config.ExpandOnBreakSeconds = value);
    }

    [UIValue("VisiblePlayerCount")]
    public int VisiblePlayerCount
    {
        get => Config.VisiblePlayerCount;
        set => ApplyConfig(config => config.VisiblePlayerCount = Math.Max(0, Math.Min(OverlayConfig.MaxVisiblePlayerCount, value)));
    }

    [UIAction("FormatIdentity")]
    public string FormatIdentity(object value) => value?.ToString() ?? string.Empty;

    [UIAction("SelectGeneralFilter")]
    public void SelectGeneralFilter() => SelectedFilter = "General";

    [UIAction("SelectBehaviorFilter")]
    public void SelectBehaviorFilter() => SelectedFilter = "Behavior";

    [UIAction("SelectBeatLeader")]
    public void SelectBeatLeader() => LeaderboardSource = "BeatLeader";

    [UIAction("SelectScoreSaber")]
    public void SelectScoreSaber() => LeaderboardSource = "ScoreSaber";

    [UIAction("SelectCollapsedPreview")]
    public void SelectCollapsedPreview()
    {
        if (Config.AlwaysExpand)
        {
            return;
        }

        PreviewMode = "Collapsed";
    }

    [UIAction("SelectExpandedPreview")]
    public void SelectExpandedPreview() => PreviewMode = "Expanded";

    [UIAction("ConfirmResetDefaults")]
    public void ConfirmResetDefaults()
    {
        if (plugin == null)
        {
            return;
        }

        var defaults = new OverlayConfig();
        plugin.Config.LeaderboardSource = defaults.LeaderboardSource;
        plugin.Config.PreviewExpanded = defaults.PreviewExpanded;
        plugin.Config.ShowBigRank = defaults.ShowBigRank;
        plugin.Config.BigRankScale = defaults.BigRankScale;
        plugin.Config.PlayerRowScale = defaults.PlayerRowScale;
        plugin.Config.ShowPp = defaults.ShowPp;
        plugin.Config.ShowModifiers = defaults.ShowModifiers;
        plugin.Config.ShowNames = defaults.ShowNames;
        plugin.Config.ShowAccuracy = defaults.ShowAccuracy;
        plugin.Config.ShowScore = defaults.ShowScore;
        plugin.Config.BackgroundOpacity = defaults.BackgroundOpacity;
        plugin.Config.ShowHighlight = defaults.ShowHighlight;
        plugin.Config.CollapsedShowBigRank = defaults.CollapsedShowBigRank;
        plugin.Config.CollapsedBigRankScale = defaults.CollapsedBigRankScale;
        plugin.Config.CollapsedPlayerRowScale = defaults.CollapsedPlayerRowScale;
        plugin.Config.CollapsedBackgroundOpacity = defaults.CollapsedBackgroundOpacity;
        plugin.Config.CollapsedShowHighlight = defaults.CollapsedShowHighlight;
        plugin.Config.CollapsedShowPp = defaults.CollapsedShowPp;
        plugin.Config.CollapsedShowModifiers = defaults.CollapsedShowModifiers;
        plugin.Config.CollapsedShowNames = defaults.CollapsedShowNames;
        plugin.Config.CollapsedShowAccuracy = defaults.CollapsedShowAccuracy;
        plugin.Config.CollapsedShowScore = defaults.CollapsedShowScore;
        plugin.Config.ExpandedShowBigRank = defaults.ExpandedShowBigRank;
        plugin.Config.ExpandedBigRankScale = defaults.ExpandedBigRankScale;
        plugin.Config.ExpandedPlayerRowScale = defaults.ExpandedPlayerRowScale;
        plugin.Config.ExpandedBackgroundOpacity = defaults.ExpandedBackgroundOpacity;
        plugin.Config.ExpandedShowHighlight = defaults.ExpandedShowHighlight;
        plugin.Config.ExpandedShowPp = defaults.ExpandedShowPp;
        plugin.Config.ExpandedShowModifiers = defaults.ExpandedShowModifiers;
        plugin.Config.ExpandedShowNames = defaults.ExpandedShowNames;
        plugin.Config.ExpandedShowAccuracy = defaults.ExpandedShowAccuracy;
        plugin.Config.ExpandedShowScore = defaults.ExpandedShowScore;
        plugin.Config.Enabled = defaults.Enabled;
        plugin.Config.EnableDesktopOverlay = defaults.EnableDesktopOverlay;
        plugin.Config.DynamicPpScore = defaults.DynamicPpScore;
        plugin.Config.InGameOverlayScale = defaults.InGameOverlayScale;
        plugin.Config.DesktopOverlayScale = defaults.DesktopOverlayScale;
        plugin.Config.UpdateIntervalSeconds = defaults.UpdateIntervalSeconds;
        plugin.Config.AlwaysExpand = defaults.AlwaysExpand;
        plugin.Config.ExpandOnBreakSeconds = defaults.ExpandOnBreakSeconds;
        plugin.Config.VisiblePlayerCount = defaults.VisiblePlayerCount;
        plugin.Config.PositionPreset = defaults.PositionPreset;
        CommitAndRefresh();
    }

    private OverlayConfig Config => plugin?.Config ?? new OverlayConfig();

    private static string NormalizeFilter(string? value)
    {
        if (string.Equals(value, "Behavior", StringComparison.OrdinalIgnoreCase))
        {
            return "Behavior";
        }

        return "General";
    }

    private static void SetShowPpAndUpdateDynamic(OverlayConfig config, bool expanded, bool value)
    {
        config.SetShowPp(expanded, value);
        if (CountAvailableShowPpOptions(config) == 0)
        {
            config.DynamicPpScore = false;
        }
    }

    private static int CountAvailableShowPpOptions(OverlayConfig config)
    {
        return config.CountAvailableShowPpOptions();
    }

    private static bool IsOnlyAvailableShowPpOption(OverlayConfig config, bool expanded)
    {
        if (CountAvailableShowPpOptions(config) != 1)
        {
            return false;
        }

        return expanded
            ? config.ExpandedShowPp
            : !config.AlwaysExpand && config.CollapsedShowPp;
    }

    private void ApplyConfig(Action<OverlayConfig> apply)
    {
        if (plugin == null)
        {
            return;
        }

        apply(plugin.Config);
        CommitAndRefresh();
    }

    private void CommitAndRefresh()
    {
        if (plugin == null)
        {
            return;
        }

        plugin.Config.Normalize();
        plugin.ConfigManager?.Save(plugin.Config);
        plugin.RuntimeCoordinator?.RefreshOverlayConfiguration();
        previewContext?.RefreshPreview();
        ApplyLeaderboardButtonGradients();
        NotifyAll();
    }

    private void NotifyAll()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    }

    private void NotifyGeneralSettings()
    {
        Notify(nameof(PreviewMode));
        Notify(nameof(CollapsedPreviewSelected));
        Notify(nameof(ExpandedPreviewSelected));
        Notify(nameof(CollapsedPreviewUnselected));
        Notify(nameof(ExpandedPreviewUnselected));
        Notify(nameof(CollapsedPreviewColor));
        Notify(nameof(ExpandedPreviewColor));
        Notify(nameof(CollapsedPreviewHoverHint));
        Notify(nameof(PreviewModeVisible));
        Notify(nameof(DynamicPpScoreInteractable));
        Notify(nameof(DynamicPpScoreHoverHint));
        Notify(nameof(ShowBigRank));
        Notify(nameof(BigRankScale));
        Notify(nameof(PlayerRowScale));
        Notify(nameof(BackgroundOpacity));
        Notify(nameof(ShowHighlight));
        Notify(nameof(ShowPp));
        Notify(nameof(ShowModifiers));
        Notify(nameof(ShowNames));
        Notify(nameof(ShowAccuracy));
        Notify(nameof(ShowScore));
        Notify(nameof(CollapsedShowBigRank));
        Notify(nameof(CollapsedBigRankScale));
        Notify(nameof(CollapsedPlayerRowScale));
        Notify(nameof(CollapsedBackgroundOpacity));
        Notify(nameof(CollapsedShowHighlight));
        Notify(nameof(CollapsedShowPp));
        Notify(nameof(CollapsedShowModifiers));
        Notify(nameof(CollapsedShowNames));
        Notify(nameof(CollapsedShowAccuracy));
        Notify(nameof(CollapsedShowScore));
        Notify(nameof(ExpandedShowBigRank));
        Notify(nameof(ExpandedBigRankScale));
        Notify(nameof(ExpandedPlayerRowScale));
        Notify(nameof(ExpandedBackgroundOpacity));
        Notify(nameof(ExpandedShowHighlight));
        Notify(nameof(ExpandedShowPp));
        Notify(nameof(ExpandedShowModifiers));
        Notify(nameof(ExpandedShowNames));
        Notify(nameof(ExpandedShowAccuracy));
        Notify(nameof(ExpandedShowScore));
        Notify(nameof(ExpandOnBreakInteractable));
        Notify(nameof(DynamicPpScore));
        Notify(nameof(PositionPreset));
    }

    private void Notify(string propertyName)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private void ApplyLeaderboardButtonGradients()
    {
        EnsureGradientSprites();
        ApplyButtonBackground(
            beatLeaderButton,
            BeatLeaderSelected ? beatLeaderGradientSprite : null,
            BeatLeaderSelected ? Color.white : new Color(0.11f, 0.14f, 0.19f, 1f));
        ApplyButtonBackground(
            scoreSaberButton,
            ScoreSaberSelected ? scoreSaberGradientSprite : null,
            ScoreSaberSelected ? Color.white : new Color(0.17f, 0.13f, 0.09f, 1f));
    }

    private static void ApplyButtonBackground(NoTransitionsButton? button, Sprite? sprite, Color color)
    {
        if (button == null)
        {
            return;
        }

        var image = button.targetGraphic as Image ?? button.GetComponent<Image>();
        if (image == null)
        {
            return;
        }

        image.sprite = sprite;
        image.type = sprite == null ? Image.Type.Sliced : Image.Type.Simple;
        image.color = color;
    }

    private static void EnsureGradientSprites()
    {
        beatLeaderGradientSprite ??= CreateGradientSprite(
            "BL_LeaderboardButton_BeatLeader",
            new Color32(0xC8, 0x22, 0x95, 0xFF),
            new Color32(0x7B, 0x20, 0xED, 0xFF));
        scoreSaberGradientSprite ??= CreateGradientSprite(
            "BL_LeaderboardButton_ScoreSaber",
            new Color32(0xFF, 0xDD, 0x18, 0xFF),
            new Color32(0xE7, 0xAE, 0x00, 0xFF));
    }

    private static Sprite CreateGradientSprite(string name, Color32 left, Color32 right)
    {
        const int Width = 64;
        const int Height = 16;
        var texture = new Texture2D(Width, Height, TextureFormat.RGBA32, mipChain: false)
        {
            name = name,
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Bilinear
        };

        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                var t = Width <= 1 ? 0f : x / (float)(Width - 1);
                texture.SetPixel(x, y, Color.Lerp(left, right, t));
            }
        }

        texture.Apply(updateMipmaps: false, makeNoLongerReadable: true);
        return Sprite.Create(texture, new Rect(0f, 0f, Width, Height), new Vector2(0.5f, 0.5f), 100f);
    }
}
#endif
