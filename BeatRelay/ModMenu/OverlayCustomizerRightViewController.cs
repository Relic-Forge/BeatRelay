#if NETFRAMEWORK
using System;
using System.ComponentModel;
using BeatRelay.BeatSaber;
using BeatSaberMarkupLanguage.Attributes;
using BeatSaberMarkupLanguage.ViewControllers;
using UnityEngine;
using UnityEngine.UI;

namespace BeatRelay.ModMenu;

[ViewDefinition("BeatRelay.Resources.BSML.OverlayCustomizerRight.bsml")]
internal sealed class OverlayCustomizerRightViewController : BSMLAutomaticViewController, INotifyPropertyChanged
{
    private Plugin? plugin;
    private OverlayCustomizerPreviewContext? previewContext;
    private BeatSaberRuntimeCoordinator? runtimeCoordinator;
    private bool mapSelectorActive;
    private readonly System.Random random = new();

    [UIComponent("SelectedMapCoverImage")]
    private Image? selectedMapCoverImage;

    public event PropertyChangedEventHandler? PropertyChanged;

    public event Action? DidRequestMapSelection;

    [UIValue("SamplePanelVisible")]
    public bool SamplePanelVisible => !mapSelectorActive;

    public void Initialize(Plugin nextPlugin, OverlayCustomizerPreviewContext nextPreviewContext)
    {
        plugin = nextPlugin;
        if (runtimeCoordinator != null)
        {
            runtimeCoordinator.CustomizationPreviewDataApplied -= HandleCustomizationPreviewDataApplied;
        }

        runtimeCoordinator = nextPlugin.RuntimeCoordinator;
        if (runtimeCoordinator != null)
        {
            runtimeCoordinator.CustomizationPreviewDataApplied += HandleCustomizationPreviewDataApplied;
        }

        if (previewContext != null)
        {
            previewContext.SelectedMapVisualChanged -= HandleSelectedMapVisualChanged;
        }

        previewContext = nextPreviewContext;
        previewContext.SelectedMapVisualChanged += HandleSelectedMapVisualChanged;
        ApplySelectedMapCover();
        NotifyAll();
    }

    public void SetMapSelectorActive(bool active)
    {
        if (mapSelectorActive == active)
        {
            return;
        }

        mapSelectorActive = active;
        Notify(nameof(SamplePanelVisible));
    }

    [UIValue("SelectedMap")]
    public string SelectedMap => previewContext?.SelectedMap.DisplayName ?? SampleBeatmapInfo.Fallback.DisplayName;

    [UIValue("SelectedMapTitle")]
    public string SelectedMapTitle => previewContext?.SelectedMap.SongName ?? SampleBeatmapInfo.Fallback.SongName;

    [UIValue("SelectedMapAuthor")]
    public string SelectedMapAuthor => previewContext?.SelectedMap.SongAuthor ?? SampleBeatmapInfo.Fallback.SongAuthor;

    [UIValue("SelectedMapStars")]
    public string SelectedMapStars
    {
        get
        {
            var map = previewContext?.SelectedMap ?? SampleBeatmapInfo.Fallback;
            if (!map.HasBeatLeaderLookup)
            {
                return map.AllowRankedPreview
                    ? map.EstimatedStars.ToString("0.00") + " \u2B50"
                    : "Unranked";
            }

            if (plugin?.RuntimeCoordinator?.TryGetCustomizationPreviewRankInfo(map, out var ranked, out var stars) == true)
            {
                return ranked
                    ? stars.GetValueOrDefault(map.EstimatedStars).ToString("0.00") + " \u2B50"
                    : "Unranked";
            }

            return "Checking...";
        }
    }
    [UIValue("SampleAccuracy")]
    public float SampleAccuracy
    {
        get => previewContext?.Accuracy ?? 95f;
        set
        {
            if (previewContext == null)
            {
                return;
            }

            previewContext.Accuracy = Math.Max(0f, Math.Min(100f, value));
            previewContext.UpdatePreview();
            Notify(nameof(SampleAccuracy));
        }
    }

    [UIValue("SampleAccuracyText")]
    public string SampleAccuracyText => SampleAccuracy.ToString("0.00") + "%";

    [UIAction("SelectRankE")]
    public void SelectRankE() => SelectSampleRank("E");

    [UIAction("SelectRankD")]
    public void SelectRankD() => SelectSampleRank("D");

    [UIAction("SelectRankC")]
    public void SelectRankC() => SelectSampleRank("C");

    [UIAction("SelectRankB")]
    public void SelectRankB() => SelectSampleRank("B");

    [UIAction("SelectRankA")]
    public void SelectRankA() => SelectSampleRank("A");

    [UIAction("SelectRankS")]
    public void SelectRankS() => SelectSampleRank("S");

    [UIAction("SelectRankSS")]
    public void SelectRankSS() => SelectSampleRank("SS");

    private void SelectSampleRank(string rank)
    {
        if (previewContext == null)
        {
            return;
        }

        var (min, max) = ResolveRankAccuracyRange(rank);
        previewContext.Accuracy = NextAccuracy(min, max);
        previewContext.RefreshPreview();
        Notify(nameof(SampleAccuracy));
        Notify(nameof(SampleAccuracyText));
    }

    [UIAction("RefreshSamplePreview")]
    public void RefreshSamplePreview()
    {
        previewContext?.RefreshPreview();
        NotifyAll();
    }

    [UIAction("SelectPreviewMap")]
    public void SelectPreviewMap()
    {
        DidRequestMapSelection?.Invoke();
    }

    private float NextAccuracy(float minInclusive, float maxExclusive)
    {
        var sample = minInclusive + ((float)random.NextDouble() * Math.Max(0.01f, maxExclusive - minInclusive));
        return Math.Max(0f, Math.Min(100f, sample));
    }

    private static (float Min, float Max) ResolveRankAccuracyRange(string? rank)
    {
        return (rank ?? string.Empty).Trim().ToUpperInvariant() switch
        {
            "SS" => (90f, 100f),
            "S" => (80f, 90f),
            "A" => (65f, 80f),
            "B" => (50f, 65f),
            "C" => (35f, 50f),
            "D" => (20f, 35f),
            _ => (0f, 20f)
        };
    }

    private void NotifyAll()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    }

    private void Notify(string propertyName)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private void HandleSelectedMapVisualChanged()
    {
        ApplySelectedMapCover();
        NotifyAll();
    }

    private void HandleCustomizationPreviewDataApplied()
    {
        NotifyAll();
    }

    private void ApplySelectedMapCover()
    {
        if (selectedMapCoverImage == null)
        {
            return;
        }

        var sprite = previewContext?.SelectedMapCoverSprite;
        selectedMapCoverImage.sprite = sprite;
        selectedMapCoverImage.preserveAspect = true;
        selectedMapCoverImage.color = sprite == null
            ? new Color(0.08f, 0.09f, 0.14f, 0.9f)
            : Color.white;
    }
}
#endif
