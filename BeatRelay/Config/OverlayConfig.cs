using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BeatRelay.Config;

public sealed class OverlayConfig
{
    public const int MaxVisiblePlayerCount = 5;

    private const string DefaultPositionPreset = "AboveMultiplier";
    private const int CurrentScaleSemanticsVersion = 2;

    private static readonly HashSet<string> PositionPresets = new(StringComparer.OrdinalIgnoreCase)
    {
        "AboveMultiplier",
        "BelowMultiplier",
        "BelowEnergy",
        "AboveCombo",
        "BelowCombo",
        "AboveHighway",
        "UnderEnergy",
        "AboveScore",
        "AboveLane",
        "RightSide"
    };

    public bool Enabled { get; set; } = true;

    public bool EnableDesktopOverlay { get; set; }

    public int ScaleSemanticsVersion { get; set; } = CurrentScaleSemanticsVersion;

    // Legacy single-scale setting retained for migration safety.
    public double OverlayScale { get; set; } = 1.0;

    public double InGameOverlayScale { get; set; } = 1.0;

    public double DesktopOverlayScale { get; set; } = 1.0;

    public double UpdateIntervalSeconds { get; set; } = 1.0;

    public bool DynamicPpScore { get; set; } = true;

    public bool AlwaysExpand { get; set; }

    public double ExpandOnBreakSeconds { get; set; } = 3.5;

    public int VisiblePlayerCount { get; set; } = 2;

    public string LeaderboardSource { get; set; } = "BeatLeader";

    public bool PreviewExpanded { get; set; }

    public bool ShowBigRank { get; set; }

    public double BigRankScale { get; set; } = 1.25;

    public double PlayerRowScale { get; set; } = 1.0;

    public double BackgroundOpacity { get; set; }

    public bool ShowHighlight { get; set; } = true;

    public bool ShowPp { get; set; } = true;

    public bool ShowModifiers { get; set; } = true;

    public bool ShowNames { get; set; } = true;

    public bool ShowAccuracy { get; set; }

    public bool ShowScore { get; set; }

    public bool CollapsedShowBigRank { get; set; }

    public double CollapsedBigRankScale { get; set; } = 1.25;

    public double CollapsedPlayerRowScale { get; set; } = 1.0;

    public double CollapsedBackgroundOpacity { get; set; }

    public bool CollapsedShowHighlight { get; set; } = true;

    public bool CollapsedShowPp { get; set; } = true;

    public bool CollapsedShowModifiers { get; set; }

    public bool CollapsedShowNames { get; set; } = true;

    public bool CollapsedShowAccuracy { get; set; } = true;

    public bool CollapsedShowScore { get; set; }

    public bool ExpandedShowBigRank { get; set; }

    public double ExpandedBigRankScale { get; set; } = 1.25;

    public double ExpandedPlayerRowScale { get; set; } = 1.0;

    public double ExpandedBackgroundOpacity { get; set; }

    public bool ExpandedShowHighlight { get; set; } = true;

    public bool ExpandedShowPp { get; set; } = true;

    public bool ExpandedShowModifiers { get; set; } = true;

    public bool ExpandedShowNames { get; set; } = true;

    public bool ExpandedShowAccuracy { get; set; } = true;

    public bool ExpandedShowScore { get; set; }

    [JsonIgnore]
    public double NoteGapSeconds
    {
        get => ExpandOnBreakSeconds;
        set => ExpandOnBreakSeconds = value;
    }

    [JsonIgnore]
    public string ManualBeatLeaderPlayerId { get; set; } = string.Empty;

    [JsonIgnore]
    public bool DebugLogging { get; set; }

    [JsonIgnore]
    public bool UseRawPpRank { get; set; }

    [JsonIgnore]
    public bool ExpandedOnPause
    {
        get => AlwaysExpand;
        set => AlwaysExpand = value;
    }

    [JsonIgnore]
    public bool ExpandedOnNoteGap
    {
        get => true;
        set { }
    }

    public string PositionPreset { get; set; } = "AboveHighway";

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }

    public void Normalize()
    {
        Normalize(migrateLegacyScaleValues: false);
    }

    public void Normalize(bool migrateLegacyScaleValues)
    {
        if (InGameOverlayScale.Equals(1.0) && DesktopOverlayScale.Equals(1.0) && !OverlayScale.Equals(1.0))
        {
            InGameOverlayScale = OverlayScale;
            DesktopOverlayScale = OverlayScale;
        }

        MigrateLegacyValues();
        if (migrateLegacyScaleValues)
        {
            InGameOverlayScale *= 0.5;
            PlayerRowScale *= 0.5;
            CollapsedPlayerRowScale *= 0.5;
            ExpandedPlayerRowScale *= 0.5;
        }

        UpdateIntervalSeconds = Clamp(UpdateIntervalSeconds, 0.25, 3.0, 1.0);
        ExpandOnBreakSeconds = Clamp(ExpandOnBreakSeconds, 2.0, 10.0, 3.5);
        OverlayScale = Clamp(OverlayScale, 0.5, 2.0, 1.0);
        InGameOverlayScale = Clamp(InGameOverlayScale, 0.5, 2.0, 1.0);
        DesktopOverlayScale = Clamp(DesktopOverlayScale, 0.5, 2.0, 1.0);
        VisiblePlayerCount = ClampInt(VisiblePlayerCount, 0, MaxVisiblePlayerCount, 4);
        LeaderboardSource = NormalizeLeaderboardSource(LeaderboardSource);
        BigRankScale = Clamp(BigRankScale, 0.5, 2.0, 1.0);
        PlayerRowScale = Clamp(PlayerRowScale, 0.5, 2.0, 1.0);
        BackgroundOpacity = Clamp(BackgroundOpacity, 0.0, 100.0, 88.0);
        CollapsedBigRankScale = Clamp(CollapsedBigRankScale, 0.5, 2.0, 1.0);
        CollapsedPlayerRowScale = Clamp(CollapsedPlayerRowScale, 0.5, 2.0, 1.0);
        CollapsedBackgroundOpacity = Clamp(CollapsedBackgroundOpacity, 0.0, 100.0, 88.0);
        ExpandedBigRankScale = Clamp(ExpandedBigRankScale, 0.5, 2.0, 1.0);
        ExpandedPlayerRowScale = Clamp(ExpandedPlayerRowScale, 0.5, 2.0, 1.0);
        ExpandedBackgroundOpacity = Clamp(ExpandedBackgroundOpacity, 0.0, 100.0, 88.0);
        PositionPreset = NormalizePositionPreset(PositionPreset);
        ScaleSemanticsVersion = CurrentScaleSemanticsVersion;
        if (DynamicPpScore && CountAvailableShowPpOptions() == 0)
        {
            DynamicPpScore = false;
        }

        EnableDesktopOverlay = EnableDesktopOverlay;
        Enabled = Enabled;
    }

    public bool GetShowBigRank(bool expanded) => expanded ? ExpandedShowBigRank : CollapsedShowBigRank;

    public void SetShowBigRank(bool expanded, bool value)
    {
        if (expanded)
        {
            ExpandedShowBigRank = value;
        }
        else
        {
            CollapsedShowBigRank = value;
        }

        ShowBigRank = value;
    }

    public double GetBigRankScale(bool expanded) => expanded ? ExpandedBigRankScale : CollapsedBigRankScale;

    public void SetBigRankScale(bool expanded, double value)
    {
        if (expanded)
        {
            ExpandedBigRankScale = value;
        }
        else
        {
            CollapsedBigRankScale = value;
        }

        BigRankScale = value;
    }

    public double GetPlayerRowScale(bool expanded) => expanded ? ExpandedPlayerRowScale : CollapsedPlayerRowScale;

    public double GetEffectivePlayerRowScale(bool expanded) => GetPlayerRowScale(expanded) * 1.8;

    public double GetEffectiveInGameOverlayScale() => InGameOverlayScale * 2.0;

    public void SetPlayerRowScale(bool expanded, double value)
    {
        if (expanded)
        {
            ExpandedPlayerRowScale = value;
        }
        else
        {
            CollapsedPlayerRowScale = value;
        }

        PlayerRowScale = value;
    }

    public double GetBackgroundOpacity(bool expanded) => expanded ? ExpandedBackgroundOpacity : CollapsedBackgroundOpacity;

    public void SetBackgroundOpacity(bool expanded, double value)
    {
        if (expanded)
        {
            ExpandedBackgroundOpacity = value;
        }
        else
        {
            CollapsedBackgroundOpacity = value;
        }

        BackgroundOpacity = value;
    }

    public bool GetShowHighlight(bool expanded) => expanded ? ExpandedShowHighlight : CollapsedShowHighlight;

    public void SetShowHighlight(bool expanded, bool value)
    {
        if (expanded)
        {
            ExpandedShowHighlight = value;
        }
        else
        {
            CollapsedShowHighlight = value;
        }

        ShowHighlight = value;
    }

    public bool GetShowPp(bool expanded) => expanded ? ExpandedShowPp : CollapsedShowPp;

    public int CountAvailableShowPpOptions()
    {
        var count = ExpandedShowPp ? 1 : 0;
        if (!AlwaysExpand && CollapsedShowPp)
        {
            count++;
        }

        return count;
    }

    public void SetShowPp(bool expanded, bool value)
    {
        if (expanded)
        {
            ExpandedShowPp = value;
        }
        else
        {
            CollapsedShowPp = value;
        }

        ShowPp = value;
    }

    public bool GetShowModifiers(bool expanded) => expanded ? ExpandedShowModifiers : CollapsedShowModifiers;

    public void SetShowModifiers(bool expanded, bool value)
    {
        if (expanded)
        {
            ExpandedShowModifiers = value;
        }
        else
        {
            CollapsedShowModifiers = value;
        }

        ShowModifiers = value;
    }

    public bool GetShowNames(bool expanded) => expanded ? ExpandedShowNames : CollapsedShowNames;

    public void SetShowNames(bool expanded, bool value)
    {
        if (expanded)
        {
            ExpandedShowNames = value;
        }
        else
        {
            CollapsedShowNames = value;
        }

        ShowNames = value;
    }

    public bool GetShowAccuracy(bool expanded) => expanded ? ExpandedShowAccuracy : CollapsedShowAccuracy;

    public void SetShowAccuracy(bool expanded, bool value)
    {
        if (expanded)
        {
            ExpandedShowAccuracy = value;
        }
        else
        {
            CollapsedShowAccuracy = value;
        }

        ShowAccuracy = value;
    }

    public bool GetShowScore(bool expanded) => expanded ? ExpandedShowScore : CollapsedShowScore;

    public void SetShowScore(bool expanded, bool value)
    {
        if (expanded)
        {
            ExpandedShowScore = value;
        }
        else
        {
            CollapsedShowScore = value;
        }

        ShowScore = value;
    }

    private void MigrateLegacyValues()
    {
        if (ExtensionData == null || ExtensionData.Count == 0)
        {
            return;
        }

        if (TryReadDouble("NoteGapSeconds", out var noteGapSeconds))
        {
            ExpandOnBreakSeconds = noteGapSeconds;
        }

        if (TryReadDouble("InGameOverlayScale", out var inGameScale))
        {
            InGameOverlayScale = inGameScale;
        }

        if (TryReadDouble("DesktopOverlayScale", out var desktopScale))
        {
            DesktopOverlayScale = desktopScale;
        }

        if (TryReadString("PositionPreset", out var preset))
        {
            PositionPreset = NormalizePositionPreset(preset);
        }

        if (TryReadString("LeaderboardSource", out var leaderboardSource))
        {
            LeaderboardSource = NormalizeLeaderboardSource(leaderboardSource);
        }

        ExtensionData.Clear();
    }

    private bool TryReadDouble(string key, out double value)
    {
        value = 0;
        if (ExtensionData == null || !ExtensionData.TryGetValue(key, out var element))
        {
            return false;
        }

        if (element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out value))
        {
            return true;
        }

        if (element.ValueKind == JsonValueKind.String && double.TryParse(element.GetString(), out value))
        {
            return true;
        }

        return false;
    }

    private bool TryReadString(string key, out string value)
    {
        value = string.Empty;
        if (ExtensionData == null || !ExtensionData.TryGetValue(key, out var element))
        {
            return false;
        }

        if (element.ValueKind == JsonValueKind.String)
        {
            value = element.GetString() ?? string.Empty;
            return true;
        }

        value = element.ToString();
        return !string.IsNullOrWhiteSpace(value);
    }

    private static string NormalizePositionPreset(string? preset)
    {
        if (string.IsNullOrWhiteSpace(preset))
        {
            return DefaultPositionPreset;
        }

        var normalized = preset.Trim();
        if (normalized.Equals("UpperRight", StringComparison.OrdinalIgnoreCase))
        {
            return DefaultPositionPreset;
        }

        if (normalized.Equals("UnderEnergy", StringComparison.OrdinalIgnoreCase))
        {
            return "BelowEnergy";
        }

        if (normalized.Equals("AboveLane", StringComparison.OrdinalIgnoreCase))
        {
            return "AboveHighway";
        }

        return PositionPresets.Contains(normalized) ? normalized : DefaultPositionPreset;
    }

    private static string NormalizeLeaderboardSource(string? source)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return "BeatLeader";
        }

        return source.Trim().Equals("ScoreSaber", StringComparison.OrdinalIgnoreCase)
            ? "ScoreSaber"
            : "BeatLeader";
    }

    private static double Clamp(double value, double min, double max, double fallback)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            return fallback;
        }

        return value < min ? min : value > max ? max : value;
    }

    private static int ClampInt(int value, int min, int max, int fallback)
    {
        if (min > max)
        {
            return fallback;
        }

        return value < min ? min : value > max ? max : value;
    }
}
