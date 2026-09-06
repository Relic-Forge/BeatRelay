using System;
using System.Globalization;

namespace BeatRelay.ScoreSaber;

public sealed class ScoreSaberEndpointBuilder
{
    public ScoreSaberEndpointBuilder(string apiBase = "https://scoresaber.com")
    {
        if (string.IsNullOrWhiteSpace(apiBase))
        {
            throw new ArgumentException("API base is required.", nameof(apiBase));
        }

        ApiBase = apiBase.TrimEnd('/');
    }

    public string ApiBase { get; }

    public Uri BuildLeaderboardInfoUri(string hash, string difficulty, string mode, int? realmId = null)
    {
        var path = $"/api/v2/leaderboards/hash/{Escape(NormalizeHash(hash))}/{Escape(ToScoreSaberMode(mode))}/{ToScoreSaberDifficulty(difficulty)}";
        return new Uri($"{ApiBase}{path}{BuildRealmQuery(realmId)}");
    }

    public Uri BuildLeaderboardScoresUri(
        string hash,
        string difficulty,
        string mode,
        int page,
        int limit,
        int? realmId = null,
        bool includePlayerScore = false)
    {
        if (page < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(page), "Page must be 1 or greater.");
        }

        if (limit < 1 || limit > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), "Limit must be between 1 and 100.");
        }

        var path = $"/api/v2/leaderboards/hash/{Escape(NormalizeHash(hash))}/{Escape(ToScoreSaberMode(mode))}/{ToScoreSaberDifficulty(difficulty)}/scores";
        var query = $"page={page.ToString(CultureInfo.InvariantCulture)}&limit={limit.ToString(CultureInfo.InvariantCulture)}&sort=score&sortDirection=desc";
        if (includePlayerScore)
        {
            query += "&includePlayerScore=true";
        }

        return new Uri($"{ApiBase}{path}?{query}");
    }

    public Uri BuildPlayerScoresUri(string playerId, string leaderboardId, int limit = 1, int? realmId = null)
    {
        if (string.IsNullOrWhiteSpace(leaderboardId))
        {
            throw new ArgumentException("Leaderboard ID is required.", nameof(leaderboardId));
        }

        var query = $"leaderboardId={Escape(leaderboardId)}&limit={Math.Max(1, limit).ToString(CultureInfo.InvariantCulture)}&page=1&sort=top";
        return new Uri($"{ApiBase}/api/v2/players/{Escape(playerId)}/scores?{query}");
    }

    public static int ToScoreSaberDifficulty(string difficulty)
    {
        var normalized = NormalizeDifficulty(difficulty);
        return normalized switch
        {
            "easy" => 1,
            "normal" => 3,
            "hard" => 5,
            "expert" => 7,
            "expertplus" => 9,
            _ => int.TryParse(difficulty, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed > 0
                ? parsed
                : throw new ArgumentException("Unsupported ScoreSaber difficulty: " + difficulty, nameof(difficulty))
        };
    }

    public static string ToScoreSaberMode(string mode)
    {
        if (string.IsNullOrWhiteSpace(mode))
        {
            return "SoloStandard";
        }

        var trimmed = mode.Trim();
        if (trimmed.StartsWith("Solo", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed;
        }

        return "Solo" + trimmed.Replace(" ", string.Empty);
    }

    private static string NormalizeDifficulty(string value)
    {
        return (value ?? string.Empty)
            .Trim()
            .Replace("+", "Plus")
            .Replace(" ", string.Empty)
            .ToLowerInvariant();
    }

    private static string NormalizeHash(string hash)
    {
        if (string.IsNullOrWhiteSpace(hash))
        {
            throw new ArgumentException("Hash is required.", nameof(hash));
        }

        return hash.Trim().ToLowerInvariant();
    }

    private static string BuildRealmQuery(int? realmId)
    {
        return realmId.HasValue
            ? "?realmId=" + realmId.Value.ToString(CultureInfo.InvariantCulture)
            : string.Empty;
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
