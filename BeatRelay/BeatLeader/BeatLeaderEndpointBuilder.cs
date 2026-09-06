using System;

namespace BeatRelay.BeatLeader;

public sealed class BeatLeaderEndpointBuilder
{
    public BeatLeaderEndpointBuilder(string apiBase = "https://api.beatleader.com")
    {
        if (string.IsNullOrWhiteSpace(apiBase))
        {
            throw new ArgumentException("API base is required.", nameof(apiBase));
        }

        ApiBase = apiBase.TrimEnd('/');
    }

    public string ApiBase { get; }

    public Uri BuildLeaderboardScoresUri(string hash, string difficulty, string mode, int page, int count)
    {
        if (page < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(page), "Page must be 1 or greater.");
        }

        if (count < 1 || count > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(count), "Count must be between 1 and 100.");
        }

        var path = $"/v3/scores/{Escape(NormalizeHash(hash))}/{Escape(difficulty)}/{Escape(mode)}/modifiers/global/page";
        return new Uri($"{ApiBase}{path}?page={page}&count={count}");
    }

    public Uri BuildPlayerBestUri(string leaderboardContext, string playerId, string hash, string difficulty, string mode)
    {
        var path = $"/score/{Escape(leaderboardContext)}/{Escape(playerId)}/{Escape(NormalizeHash(hash))}/{Escape(difficulty)}/{Escape(mode)}";
        return new Uri($"{ApiBase}{path}");
    }

    public Uri BuildOAuthIdentityUri()
    {
        return new Uri($"{ApiBase}/oauth2/identity");
    }

    public Uri BuildLeaderboardDetailUri(string leaderboardId, int page, int count)
    {
        if (string.IsNullOrWhiteSpace(leaderboardId))
        {
            throw new ArgumentException("Leaderboard ID is required.", nameof(leaderboardId));
        }

        if (page < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(page), "Page must be 1 or greater.");
        }

        if (count < 1 || count > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(count), "Count must be between 1 and 100.");
        }

        var path = $"/leaderboard/{Escape(leaderboardId)}";
        return new Uri($"{ApiBase}{path}?page={page}&count={count}");
    }

    public Uri BuildSongByHashUri(string hash)
    {
        var path = $"/map/hash/{Escape(NormalizeHash(hash))}";
        return new Uri($"{ApiBase}{path}");
    }

    private static string NormalizeHash(string hash)
    {
        if (string.IsNullOrWhiteSpace(hash))
        {
            throw new ArgumentException("Hash is required.", nameof(hash));
        }

        return hash.Trim().ToLowerInvariant();
    }

    private static string Escape(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Path value is required.", nameof(value));
        }

        return Uri.EscapeDataString(value.Trim());
    }
}
