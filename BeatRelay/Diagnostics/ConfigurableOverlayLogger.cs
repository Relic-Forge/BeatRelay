namespace BeatRelay.Diagnostics;

public sealed class ConfigurableOverlayLogger : IOverlayLogger
{
    private static readonly System.Collections.Generic.HashSet<string> VerboseInfoEvents =
        new(System.StringComparer.OrdinalIgnoreCase)
        {
            "runtime_overlay_snapshot",
            "runtime_session_start_result",
            "session_page_fetched",
            "performance_menu_registration",
            "performance_renderer_summary"
        };

    private readonly IOverlayLogger innerLogger;
    private readonly bool debugEnabled;

    public ConfigurableOverlayLogger(IOverlayLogger innerLogger, bool debugEnabled)
    {
        this.innerLogger = innerLogger ?? throw new System.ArgumentNullException(nameof(innerLogger));
        this.debugEnabled = debugEnabled;
    }

    public void Info(string eventName, string message)
    {
        if (!debugEnabled && VerboseInfoEvents.Contains(eventName))
        {
            return;
        }

        innerLogger.Info(eventName, message);
    }

    public void Warn(string eventName, string message)
    {
        innerLogger.Warn(eventName, message);
    }

    public void Error(string eventName, string message)
    {
        innerLogger.Error(eventName, message);
    }
}
