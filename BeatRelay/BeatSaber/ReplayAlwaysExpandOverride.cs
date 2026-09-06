using System;
using BeatRelay.Config;

namespace BeatRelay.BeatSaber;

public sealed class ReplayAlwaysExpandOverride
{
    private readonly OverlayConfig config;
    private bool active;
    private bool previousAlwaysExpand;

    public ReplayAlwaysExpandOverride(OverlayConfig config)
    {
        this.config = config ?? throw new ArgumentNullException(nameof(config));
    }

    public bool IsActive => active;

    public bool PreviousAlwaysExpand => previousAlwaysExpand;

    public void Enable()
    {
        if (!active)
        {
            previousAlwaysExpand = config.AlwaysExpand;
            active = true;
        }

        config.AlwaysExpand = true;
    }

    public void Restore()
    {
        if (!active)
        {
            return;
        }

        config.AlwaysExpand = previousAlwaysExpand;
        active = false;
    }
}
