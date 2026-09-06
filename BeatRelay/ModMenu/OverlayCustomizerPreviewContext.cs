#if NETFRAMEWORK
using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using BeatRelay.UI;
using UnityEngine;

namespace BeatRelay.ModMenu;

internal sealed class OverlayCustomizerPreviewContext
{
    private readonly Plugin plugin;

    public event Action? SelectedMapVisualChanged;

    public OverlayCustomizerPreviewContext(Plugin plugin)
    {
        this.plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));
        SelectedMap = SampleBeatmapInfo.Fallback;
    }

    public SampleBeatmapInfo SelectedMap { get; set; }

    public Sprite? SelectedMapCoverSprite { get; private set; }

    public float Accuracy { get; set; } = 95f;

    public OverlayViewModel BuildViewModel()
    {
        return plugin.RuntimeCoordinator?.BuildCustomizationPreviewViewModel(
            SelectedMap,
            Accuracy,
            plugin.Config.AlwaysExpand || plugin.Config.PreviewExpanded) ?? OverlayViewModel.Hidden();
    }

    public void UpdatePreview()
    {
        plugin.RuntimeCoordinator?.UpdateCustomizationPreview(BuildViewModel());
    }

    public void RefreshPreview()
    {
        var expanded = plugin.Config.AlwaysExpand || plugin.Config.PreviewExpanded;
        var runtimeCoordinator = plugin.RuntimeCoordinator;
        if (runtimeCoordinator == null)
        {
            return;
        }

        if (SelectedMap.HasBeatLeaderLookup && !runtimeCoordinator.HasCustomizationPreviewCache(SelectedMap))
        {
            runtimeCoordinator.UpdateCustomizationPreview(runtimeCoordinator.BuildCustomizationPreviewLoadingViewModel(SelectedMap, expanded));
            runtimeCoordinator.RefreshCustomizationPreviewData(SelectedMap, Accuracy, expanded);
            return;
        }

        runtimeCoordinator.UpdateCustomizationPreview(BuildViewModel());
        runtimeCoordinator.RefreshCustomizationPreviewData(SelectedMap, Accuracy, expanded);
    }

    public void SelectMap(BeatmapLevel level, BeatmapKey key)
    {
        var basicData = key.IsValid()
            ? level.GetDifficultyBeatmapData(key.beatmapCharacteristic, key.difficulty)
            : null;
        var notes = basicData?.cuttableObjectsCount > 0
            ? basicData.cuttableObjectsCount
            : basicData?.notesCount > 0 ? basicData.notesCount : EstimateNotesFromDuration(level.songDuration);
        var levelId = level.levelID ?? string.Empty;
        var resolvedHash = ExtractHash(levelId, level);
        var allowRankedPreview = !string.IsNullOrWhiteSpace(resolvedHash) && resolvedHash.Length >= 32;
        SelectedMap = new SampleBeatmapInfo(
            string.IsNullOrWhiteSpace(level.songName) ? "Selected Map" : level.songName.Trim(),
            string.IsNullOrWhiteSpace(level.songAuthorName) ? "Unknown Artist" : level.songAuthorName.Trim(),
            key.IsValid() ? key.difficulty.ToString() : "Normal",
            Math.Max(1, notes),
            level.songDuration > 0 ? level.songDuration : 180f,
            level.beatsPerMinute > 0 ? level.beatsPerMinute : 120f,
            levelId,
            allowRankedPreview ? resolvedHash : string.Empty,
            ResolveModeName(key),
            allowRankedPreview);
        SelectedMapCoverSprite = null;
        SelectedMapVisualChanged?.Invoke();
        _ = LoadSelectedMapCoverAsync(level);
    }

    private async Task LoadSelectedMapCoverAsync(BeatmapLevel level)
    {
        try
        {
            var sprite = level.previewMediaData == null
                ? null
                : await level.previewMediaData.GetCoverSpriteAsync();
            if (string.Equals(SelectedMap.Key, level.levelID, StringComparison.OrdinalIgnoreCase))
            {
                SelectedMapCoverSprite = sprite;
                SelectedMapVisualChanged?.Invoke();
            }
        }
        catch
        {
        }
    }

    private static int EstimateNotesFromDuration(float durationSeconds)
    {
        return Math.Max(250, (int)Math.Round(Math.Max(90f, durationSeconds) * 4.5f));
    }

    private static string ExtractHash(string levelId, object? beatmapLevel)
    {
        var explicitHash = ReadString(beatmapLevel, "hash", "_hash", "levelHash", "_levelHash");
        if (!string.IsNullOrWhiteSpace(explicitHash))
        {
            return explicitHash.Trim().ToLowerInvariant();
        }

        var normalizedLevelId = levelId?.Trim() ?? string.Empty;
        var hashCandidate = new string(normalizedLevelId.Where(char.IsLetterOrDigit).ToArray());
        if (hashCandidate.Length >= 40)
        {
            var tail = hashCandidate.Substring(hashCandidate.Length - 40);
            if (tail.All(Uri.IsHexDigit))
            {
                return tail.ToLowerInvariant();
            }
        }

        const string customPrefix = "custom_level_";
        return normalizedLevelId.StartsWith(customPrefix, StringComparison.OrdinalIgnoreCase)
            ? normalizedLevelId.Substring(customPrefix.Length).ToLowerInvariant()
            : normalizedLevelId.ToLowerInvariant();
    }

    private static string? ReadString(object? source, params string[] names)
    {
        if (source == null)
        {
            return null;
        }

        var type = source.GetType();
        foreach (var name in names)
        {
            var property = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (property?.GetValue(source) is string propertyValue && !string.IsNullOrWhiteSpace(propertyValue))
            {
                return propertyValue;
            }

            var field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (field?.GetValue(source) is string fieldValue && !string.IsNullOrWhiteSpace(fieldValue))
            {
                return fieldValue;
            }
        }

        return null;
    }

    private static string ResolveModeName(BeatmapKey key)
    {
        if (!key.IsValid())
        {
            return "Standard";
        }

        var serializedName = key.beatmapCharacteristic?.serializedName;
        return string.IsNullOrWhiteSpace(serializedName) ? "Standard" : serializedName;
    }

}

public sealed class SampleBeatmapInfo
{
    public static readonly SampleBeatmapInfo Fallback = new("Sample Map", "Song Author", "ExpertPlus", 900, 180f, 120f, "sample", string.Empty, "Standard", true, 12.48d, 8d, 14d, 5d);

    public SampleBeatmapInfo(string songName, string difficulty, int noteCount, float durationSeconds, float beatsPerMinute, string key)
        : this(songName, "Unknown Artist", difficulty, noteCount, durationSeconds, beatsPerMinute, key, string.Empty, "Standard", false)
    {
    }

    public SampleBeatmapInfo(string songName, string difficulty, int noteCount, float durationSeconds, float beatsPerMinute, string key, string hash, string mode)
        : this(songName, "Unknown Artist", difficulty, noteCount, durationSeconds, beatsPerMinute, key, hash, mode, false)
    {
    }

    public SampleBeatmapInfo(
        string songName,
        string songAuthor,
        string difficulty,
        int noteCount,
        float durationSeconds,
        float beatsPerMinute,
        string key,
        string hash,
        string mode,
        bool allowRankedPreview,
        double? fixedStarRating = null,
        double? beatLeaderPassRating = null,
        double? beatLeaderAccRating = null,
        double? beatLeaderTechRating = null)
    {
        SongName = string.IsNullOrWhiteSpace(songName) ? "Sample Map" : songName.Trim();
        SongAuthor = string.IsNullOrWhiteSpace(songAuthor) ? "Unknown Artist" : songAuthor.Trim();
        Difficulty = string.IsNullOrWhiteSpace(difficulty) ? "Normal" : difficulty.Trim();
        NoteCount = Math.Max(1, noteCount);
        DurationSeconds = Math.Max(1f, durationSeconds);
        BeatsPerMinute = Math.Max(1f, beatsPerMinute);
        Key = string.IsNullOrWhiteSpace(key) ? SongName : key;
        Hash = string.IsNullOrWhiteSpace(hash) ? string.Empty : hash.Trim().ToLowerInvariant();
        Mode = string.IsNullOrWhiteSpace(mode) ? "Standard" : mode.Trim();
        AllowRankedPreview = allowRankedPreview;
        FixedStarRating = fixedStarRating.HasValue && fixedStarRating.Value > 0d ? fixedStarRating : null;
        BeatLeaderPassRating = beatLeaderPassRating.HasValue && beatLeaderPassRating.Value > 0d ? beatLeaderPassRating : null;
        BeatLeaderAccRating = beatLeaderAccRating.HasValue && beatLeaderAccRating.Value > 0d ? beatLeaderAccRating : null;
        BeatLeaderTechRating = beatLeaderTechRating.HasValue && beatLeaderTechRating.Value > 0d ? beatLeaderTechRating : null;
    }

    public string SongName { get; }

    public string SongAuthor { get; }

    public string Difficulty { get; }

    public int NoteCount { get; }

    public float DurationSeconds { get; }

    public float BeatsPerMinute { get; }

    public string Key { get; }

    public string Hash { get; }

    public string Mode { get; }

    public bool AllowRankedPreview { get; }

    public bool HasBeatLeaderLookup => AllowRankedPreview && !string.IsNullOrWhiteSpace(Hash) && Hash.Length >= 32;

    public double? FixedStarRating { get; }

    public double? BeatLeaderPassRating { get; }

    public double? BeatLeaderAccRating { get; }

    public double? BeatLeaderTechRating { get; }

    public bool HasBeatLeaderRatingComponents =>
        BeatLeaderPassRating.GetValueOrDefault() > 0d
        && BeatLeaderAccRating.GetValueOrDefault() > 0d
        && BeatLeaderTechRating.GetValueOrDefault() > 0d;

    public double EstimatedStars
    {
        get
        {
            if (FixedStarRating.HasValue)
            {
                return FixedStarRating.Value;
            }

            var durationMinutes = Math.Max(0.5d, DurationSeconds / 60d);
            var notesPerSecond = NoteCount / Math.Max(1d, DurationSeconds);
            var difficultyBonus = Difficulty.IndexOf("ExpertPlus", StringComparison.OrdinalIgnoreCase) >= 0 ? 2.8d
                : Difficulty.IndexOf("Expert", StringComparison.OrdinalIgnoreCase) >= 0 ? 1.6d
                : Difficulty.IndexOf("Hard", StringComparison.OrdinalIgnoreCase) >= 0 ? 0.7d
                : 0.2d;
            var density = Math.Pow(Math.Max(0.1d, notesPerSecond), 1.18d) * 1.35d;
            var lengthBonus = Math.Min(1.4d, durationMinutes * 0.22d);
            return Math.Max(1d, Math.Min(16d, density + difficultyBonus + lengthBonus));
        }
    }

    public string DisplayName => SongName;
}
#endif
