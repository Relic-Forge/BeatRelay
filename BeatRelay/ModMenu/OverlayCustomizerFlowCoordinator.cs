#if NETFRAMEWORK
using System;
using BeatSaberMarkupLanguage;
using HMUI;
using UnityEngine;

namespace BeatRelay.ModMenu;

internal sealed class OverlayCustomizerFlowCoordinator : FlowCoordinator
{
    private Plugin? plugin;
    private OverlayCustomizerPreviewContext? previewContext;
    private OverlayCustomizerLeftViewController? leftViewController;
    private OverlayCustomizerCenterViewController? centerViewController;
    private OverlayCustomizerRightViewController? rightViewController;
    private OverlaySampleLevelSelectionFlowCoordinator? sampleLevelSelectionFlowCoordinator;
    private bool refreshPreviewAfterSampleSelection;

    public event Action? DidClose;

    public void Initialize(Plugin nextPlugin)
    {
        plugin = nextPlugin;
        previewContext = new OverlayCustomizerPreviewContext(nextPlugin);
        if (leftViewController != null && rightViewController != null)
        {
            leftViewController.Initialize(nextPlugin, previewContext);
            rightViewController.Initialize(nextPlugin, previewContext);
        }
    }

    protected override void DidActivate(bool firstActivation, bool addedToHierarchy, bool screenSystemEnabling)
    {
        if (plugin == null || previewContext == null)
        {
            return;
        }

        if (firstActivation)
        {
            SetTitle("BeatRelay");
            showBackButton = true;
            leftViewController = BeatSaberUI.CreateViewController<OverlayCustomizerLeftViewController>();
            centerViewController = BeatSaberUI.CreateViewController<OverlayCustomizerCenterViewController>();
            rightViewController = BeatSaberUI.CreateViewController<OverlayCustomizerRightViewController>();
            leftViewController.Initialize(plugin, previewContext);
            rightViewController.Initialize(plugin, previewContext);
            ProvideInitialViewControllers(centerViewController, leftViewController, rightViewController);
        }

        if (rightViewController != null)
        {
            rightViewController.DidRequestMapSelection -= PresentSampleLevelSelection;
            rightViewController.DidRequestMapSelection += PresentSampleLevelSelection;
        }

        plugin.RuntimeCoordinator?.SetCustomizationPreviewVisible(true, previewContext.BuildViewModel());
    }

    protected override void DidDeactivate(bool removedFromHierarchy, bool screenSystemDisabling)
    {
        if (removedFromHierarchy && !screenSystemDisabling)
        {
            plugin?.RuntimeCoordinator?.SetCustomizationPreviewVisible(false);
            DidClose?.Invoke();
        }

        if (removedFromHierarchy && rightViewController != null)
        {
            rightViewController.DidRequestMapSelection -= PresentSampleLevelSelection;
        }
    }

    protected override void BackButtonWasPressed(ViewController topViewController)
    {
        plugin?.RuntimeCoordinator?.SetCustomizationPreviewVisible(false);
        DidClose?.Invoke();
        try
        {
            BeatSaberUI.MainFlowCoordinator.DismissFlowCoordinator(this);
        }
        catch (Exception ex)
        {
            Debug.LogError("[BeatRelay] Failed to dismiss customizer: " + ex);
        }
    }

    private void PresentSampleLevelSelection()
    {
        if (previewContext == null)
        {
            return;
        }

        try
        {
            if (sampleLevelSelectionFlowCoordinator != null)
            {
                sampleLevelSelectionFlowCoordinator.DidSelectPreviewLevelEvent -= HandlePreviewLevelSelected;
                sampleLevelSelectionFlowCoordinator.DidFinishEvent -= HandlePreviewLevelSelectionFinished;
                sampleLevelSelectionFlowCoordinator = null;
            }

            sampleLevelSelectionFlowCoordinator = BeatSaberUI.CreateFlowCoordinator<OverlaySampleLevelSelectionFlowCoordinator>();
            sampleLevelSelectionFlowCoordinator.DidSelectPreviewLevelEvent += HandlePreviewLevelSelected;
            sampleLevelSelectionFlowCoordinator.DidFinishEvent += HandlePreviewLevelSelectionFinished;
            refreshPreviewAfterSampleSelection = false;
            rightViewController?.SetMapSelectorActive(true);
            plugin?.RuntimeCoordinator?.SetCustomizationPreviewWorldVisible(false);

            var beatmapKey = default(BeatmapKey);
            var category = SelectLevelCategoryViewController.LevelCategory.All;
            sampleLevelSelectionFlowCoordinator.Setup(
                new LevelSelectionFlowCoordinator.State(category, null, in beatmapKey, null));
            PresentFlowCoordinator(sampleLevelSelectionFlowCoordinator);
        }
        catch (Exception ex)
        {
            Debug.LogError("[BeatRelay] Failed to present preview map selector: " + ex);
            CleanupSampleLevelSelection();
        }
    }

    private void HandlePreviewLevelSelected(BeatmapLevel level, BeatmapKey key)
    {
        if (previewContext == null || sampleLevelSelectionFlowCoordinator == null)
        {
            return;
        }

        if (level == null || !key.IsValid())
        {
            return;
        }

        previewContext.SelectMap(level, key);
        refreshPreviewAfterSampleSelection = true;
        DismissFlowCoordinator(sampleLevelSelectionFlowCoordinator, finishedCallback: CleanupSampleLevelSelection);
    }

    private void HandlePreviewLevelSelectionFinished()
    {
        if (sampleLevelSelectionFlowCoordinator != null)
        {
            DismissFlowCoordinator(sampleLevelSelectionFlowCoordinator, finishedCallback: CleanupSampleLevelSelection);
        }
    }

    private void CleanupSampleLevelSelection()
    {
        if (sampleLevelSelectionFlowCoordinator == null)
        {
            return;
        }

        sampleLevelSelectionFlowCoordinator.DidSelectPreviewLevelEvent -= HandlePreviewLevelSelected;
        sampleLevelSelectionFlowCoordinator.DidFinishEvent -= HandlePreviewLevelSelectionFinished;
        sampleLevelSelectionFlowCoordinator = null;
        rightViewController?.SetMapSelectorActive(false);
        plugin?.RuntimeCoordinator?.SetCustomizationPreviewWorldVisible(true);
        if (previewContext != null)
        {
            if (refreshPreviewAfterSampleSelection)
            {
                previewContext.RefreshPreview();
            }
            else
            {
                previewContext.UpdatePreview();
            }

            if (plugin != null)
            {
                rightViewController?.Initialize(plugin, previewContext);
            }
        }

        refreshPreviewAfterSampleSelection = false;
    }
}
#endif
