#if NETFRAMEWORK
using System;
using HMUI;

namespace BeatRelay.ModMenu;

internal sealed class OverlaySampleLevelSelectionFlowCoordinator : LevelSelectionFlowCoordinator
{
    protected override bool hidePracticeButton => true;

    protected override bool hidePacksIfOneOrNone => false;

    protected override bool showBackButtonForMainViewController => true;

    protected override string actionButtonText => "Use for Preview";

    protected override string mainTitle => "Select Preview Map";

    protected override bool enableCustomLevels => true;

    public event Action<BeatmapLevel, BeatmapKey>? DidSelectPreviewLevelEvent;

    public event Action? DidFinishEvent;

    protected override void ActionButtonWasPressed()
    {
        var level = selectedBeatmapLevel;
        var key = selectedBeatmapKey;
        if (level == null || !key.IsValid())
        {
            return;
        }

        DidSelectPreviewLevelEvent?.Invoke(level, key);
    }

    protected override void BackButtonWasPressed(ViewController topViewController)
    {
        if (isInRootViewController)
        {
            DidFinishEvent?.Invoke();
            return;
        }

        base.BackButtonWasPressed(topViewController);
    }
}
#endif
