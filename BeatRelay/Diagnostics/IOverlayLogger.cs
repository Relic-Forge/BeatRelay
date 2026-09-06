namespace BeatRelay.Diagnostics;

public interface IOverlayLogger
{
    void Info(string eventName, string message);

    void Warn(string eventName, string message);

    void Error(string eventName, string message);
}
