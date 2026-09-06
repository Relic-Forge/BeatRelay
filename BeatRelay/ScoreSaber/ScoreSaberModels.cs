using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Serialization;
using BeatRelay.BeatLeader;

namespace BeatRelay.ScoreSaber;

public sealed class ScoreSaberLeaderboardResponse
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("map")]
    public ScoreSaberMapDto? Map { get; set; }

    [JsonPropertyName("difficulty")]
    public ScoreSaberDifficultyDto? Difficulty { get; set; }

    [JsonPropertyName("maxScore")]
    public int? MaxScore { get; set; }

    [JsonPropertyName("totalScores")]
    public int TotalScores { get; set; }

    [JsonPropertyName("realm")]
    public ScoreSaberRealmDto? Realm { get; set; }
}

public sealed class ScoreSaberScoresResponse
{
    [JsonPropertyName("data")]
    public List<ScoreSaberScoreDto> Data { get; set; } = new();

    [JsonPropertyName("metadata")]
    public ScoreSaberMetadataDto? Metadata { get; set; }

    [JsonPropertyName("playerScore")]
    public ScoreSaberScoreDto? PlayerScore { get; set; }
}

public sealed class ScoreSaberPlayerScoresResponse
{
    [JsonPropertyName("data")]
    public List<ScoreSaberPlayerScoreDto> Data { get; set; } = new();

    [JsonPropertyName("metadata")]
    public ScoreSaberMetadataDto? Metadata { get; set; }
}

public sealed class ScoreSaberPlayerScoreDto
{
    [JsonPropertyName("score")]
    public ScoreSaberScoreDto? Score { get; set; }

    [JsonPropertyName("leaderboard")]
    public ScoreSaberLeaderboardResponse? Leaderboard { get; set; }
}

public sealed class ScoreSaberMapDto
{
    [JsonPropertyName("hash")]
    public string? Hash { get; set; }
}

public sealed class ScoreSaberDifficultyDto
{
    [JsonPropertyName("difficulty")]
    public int Difficulty { get; set; }

    [JsonPropertyName("rawDifficulty")]
    public string? RawDifficulty { get; set; }

    [JsonPropertyName("gameMode")]
    public string? GameMode { get; set; }
}

public sealed class ScoreSaberRealmDto
{
    [JsonPropertyName("realmId")]
    public int RealmId { get; set; }

    [JsonPropertyName("realmName")]
    public string? RealmName { get; set; }

    [JsonPropertyName("leaderboardStatus")]
    public string? LeaderboardStatus { get; set; }

    [JsonPropertyName("positiveModifiers")]
    public bool PositiveModifiers { get; set; }

    [JsonPropertyName("stars")]
    public double? Stars { get; set; }
}

public sealed class ScoreSaberMetadataDto
{
    [JsonPropertyName("page")]
    public int Page { get; set; }

    [JsonPropertyName("itemsPerPage")]
    public int ItemsPerPage { get; set; }

    [JsonPropertyName("totalItems")]
    public int TotalItems { get; set; }
}

public sealed class ScoreSaberScoreDto
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("rank")]
    public int Rank { get; set; }

    [JsonPropertyName("unmodifiedScore")]
    public int? UnmodifiedScore { get; set; }

    [JsonPropertyName("modifiedScore")]
    public int ModifiedScore { get; set; }

    [JsonPropertyName("accuracy")]
    public double? Accuracy { get; set; }

    [JsonPropertyName("pp")]
    public double? Pp { get; set; }

    [JsonPropertyName("mods")]
    public List<string>? Mods { get; set; }

    [JsonPropertyName("player")]
    public ScoreSaberPlayerDto? Player { get; set; }

    public BeatLeaderScoreDto ToBeatLeaderScoreDto()
    {
        return new BeatLeaderScoreDto
        {
            Id = Id,
            Rank = Rank,
            BaseScore = UnmodifiedScore,
            ModifiedScore = ModifiedScore,
            Accuracy = Accuracy,
            Pp = Pp,
            Modifiers = Mods == null ? string.Empty : string.Join(",", Mods),
            Player = new BeatLeaderPlayerDto
            {
                Id = Player?.Id,
                Name = string.IsNullOrWhiteSpace(Player?.Name) ? Player?.PlayerNameInGame : Player?.Name,
                AvatarUrl = NormalizeAvatarUrl(Player?.ResolveAvatarUrl())
            }
        };
    }

    private static string NormalizeAvatarUrl(string? avatarUrl)
    {
        if (string.IsNullOrWhiteSpace(avatarUrl))
        {
            return string.Empty;
        }

        var trimmed = avatarUrl.Trim();
        if (trimmed.StartsWith("//", StringComparison.Ordinal))
        {
            return "https:" + trimmed;
        }

        if (trimmed.StartsWith("/", StringComparison.Ordinal))
        {
            return "https://scoresaber.com" + trimmed;
        }

        return trimmed;
    }
}

public sealed class ScoreSaberPlayerDto
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("playerNameInGame")]
    public string? PlayerNameInGame { get; set; }

    [JsonPropertyName("avatar")]
    public string? Avatar { get; set; }

    [JsonPropertyName("avatarUrl")]
    public string? AvatarUrl { get; set; }

    [JsonPropertyName("profilePicture")]
    public string? ProfilePicture { get; set; }

    [JsonPropertyName("profilePictureUrl")]
    public string? ProfilePictureUrl { get; set; }

    public string? ResolveAvatarUrl()
    {
        return !string.IsNullOrWhiteSpace(Avatar)
            ? Avatar
            : !string.IsNullOrWhiteSpace(AvatarUrl)
                ? AvatarUrl
                : !string.IsNullOrWhiteSpace(ProfilePicture)
                    ? ProfilePicture
                    : ProfilePictureUrl;
    }
}

internal static class ScoreSaberScoreDtoFormatter
{
    public static string NormalizeProfileAvatar(string? avatarUrl)
    {
        if (string.IsNullOrWhiteSpace(avatarUrl))
        {
            return string.Empty;
        }

        var trimmed = avatarUrl.Trim();
        if (trimmed.StartsWith("//", StringComparison.Ordinal))
        {
            return "https:" + trimmed;
        }

        if (trimmed.StartsWith("/", StringComparison.Ordinal))
        {
            return "https://scoresaber.com" + trimmed;
        }

        return trimmed;
    }
}

public static class ScoreSaberResponseMapper
{
    public static LeaderboardScoresResponse ToLeaderboardScoresResponse(
        string hash,
        string difficulty,
        string mode,
        ScoreSaberLeaderboardResponse leaderboard,
        ScoreSaberScoresResponse scores)
    {
        var metadata = scores.Metadata ?? new ScoreSaberMetadataDto { Page = 1, ItemsPerPage = Math.Max(1, scores.Data.Count), TotalItems = leaderboard.TotalScores };
        var stars = leaderboard.Realm?.Stars;
        var status = leaderboard.Realm?.LeaderboardStatus ?? string.Empty;
        var hasPp = stars.GetValueOrDefault() > 0d && string.Equals(status, "RANKED", StringComparison.OrdinalIgnoreCase);
        var data = new List<ScoreSaberScoreDto>(scores.Data);
        if (scores.PlayerScore != null && !ContainsScore(data, scores.PlayerScore))
        {
            data.Add(scores.PlayerScore);
        }

        return new LeaderboardScoresResponse
        {
            Metadata = new LeaderboardMetadata
            {
                Page = Math.Max(1, metadata.Page),
                ItemsPerPage = Math.Max(1, metadata.ItemsPerPage),
                Total = Math.Max(0, metadata.TotalItems > 0 ? metadata.TotalItems : leaderboard.TotalScores)
            },
            Container = new LeaderboardContainer
            {
                LeaderboardId = leaderboard.Id.ToString(CultureInfo.InvariantCulture),
                Ranked = hasPp,
                MaxScore = leaderboard.MaxScore,
                Stars = stars,
                SourceName = "ScoreSaber",
                RankByPp = false,
                SupportsPp = hasPp,
                UsesScoreSaberPpCurve = hasPp,
                PositiveModifiers = leaderboard.Realm?.PositiveModifiers == true,
                LeaderboardStatus = string.IsNullOrWhiteSpace(status) ? null : status,
                RealmId = leaderboard.Realm?.RealmId,
                RealmName = leaderboard.Realm?.RealmName
            },
            Data = data.ConvertAll(score => score.ToBeatLeaderScoreDto())
        };
    }

    private static bool ContainsScore(IReadOnlyList<ScoreSaberScoreDto> scores, ScoreSaberScoreDto candidate)
    {
        if (candidate.Id > 0 && scores.Any(score => score.Id == candidate.Id))
        {
            return true;
        }

        var candidatePlayerId = candidate.Player?.Id;
        if (candidate.Rank <= 0 || string.IsNullOrWhiteSpace(candidatePlayerId))
        {
            return false;
        }

        return candidate.Rank > 0 && scores.Any(score =>
            score.Rank == candidate.Rank
            && !string.IsNullOrWhiteSpace(score.Player?.Id)
            && string.Equals(score.Player.Id, candidatePlayerId, StringComparison.OrdinalIgnoreCase));
    }
}
