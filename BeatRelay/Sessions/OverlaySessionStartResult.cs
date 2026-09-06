using BeatRelay.Ranking;

namespace BeatRelay.Sessions;

public sealed class OverlaySessionStartResult
{
    private OverlaySessionStartResult(bool isStarted, LeaderboardSessionCache? cache, string? errorMessage)
    {
        IsStarted = isStarted;
        Cache = cache;
        ErrorMessage = errorMessage;
    }

    public bool IsStarted { get; }

    public LeaderboardSessionCache? Cache { get; }

    public string? ErrorMessage { get; }

    public static OverlaySessionStartResult Started(LeaderboardSessionCache cache) => new(true, cache, null);

    public static OverlaySessionStartResult NotStarted(string errorMessage) => new(false, null, errorMessage);
}
