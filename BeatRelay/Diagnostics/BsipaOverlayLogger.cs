#if NETFRAMEWORK
namespace BeatRelay.Diagnostics;

internal sealed class BsipaOverlayLogger : IOverlayLogger
{
    private readonly IPA.Logging.Logger logger;

    public BsipaOverlayLogger(IPA.Logging.Logger logger)
    {
        this.logger = logger ?? throw new System.ArgumentNullException(nameof(logger));
    }

    public void Info(string eventName, string message)
    {
        // The game mod logs problems only, including when diagnostic sampling is enabled.
    }

    public void Warn(string eventName, string message) => logger.Warn($"[{eventName}] {message}");

    public void Error(string eventName, string message) => logger.Error($"[{eventName}] {message}");
}
#endif
