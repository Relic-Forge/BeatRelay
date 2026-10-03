using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json;
using BeatRelay.BeatLeader;

namespace BeatRelay.ScoreSaber;

public sealed class ScoreSaberLeaderboardResponse
{
    [JsonProperty("id")]
    public long Id { get; set; }

    [JsonProperty("map")]
    public ScoreSaberMapDto? Map { get; set; }

    [JsonProperty("difficulty")]
    public ScoreSaberDifficultyDto? Difficulty { get; set; }

    [JsonProperty("maxScore")]
    public int? MaxScore { get; set; }

    [JsonProperty("totalScores")]
    public int TotalScores { get; set; }

    [JsonProperty("realm")]
    public ScoreSaberRealmDto? Realm { get; set; }
}

public sealed class ScoreSaberScoresResponse
{
    [JsonProperty("data")]
    public List<ScoreSaberScoreDto> Data { get; set; } = new();

    [JsonProperty("metadata")]
    public ScoreSaberMetadataDto? Metadata { get; set; }

    [JsonProperty("playerScore")]
    public ScoreSaberScoreDto? PlayerScore { get; set; }
}

public sealed class ScoreSaberPlayerScoresResponse
{
    [JsonProperty("data")]
    public List<ScoreSaberPlayerScoreDto> Data { get; set; } = new();

    [JsonProperty("metadata")]
    public ScoreSaberMetadataDto? Metadata { get; set; }
}

public sealed class ScoreSaberPlayerScoreDto
{
    [JsonProperty("score")]
    public ScoreSaberScoreDto? Score { get; set; }

    [JsonProperty("leaderboard")]
    public ScoreSaberLeaderboardResponse? Leaderboard { get; set; }
}

public sealed class ScoreSaberMapDto
{
    [JsonProperty("hash")]
    public string? Hash { get; set; }
}

public sealed class ScoreSaberDifficultyDto
{
    [JsonProperty("difficulty")]
    public int Difficulty { get; set; }

    [JsonProperty("rawDifficulty")]
    public string? RawDifficulty { get; set; }

    [JsonProperty("gameMode")]
    public string? GameMode { get; set; }
}

public sealed class ScoreSaberRealmDto
{
    [JsonProperty("realmId")]
    public int RealmId { get; set; }

    [JsonProperty("realmName")]
    public string? RealmName { get; set; }

    [JsonProperty("leaderboardStatus")]
    public string? LeaderboardStatus { get; set; }

    [JsonProperty("positiveModifiers")]
    public bool PositiveModifiers { get; set; }

    [JsonProperty("stars")]
    public double? Stars { get; set; }
}

public sealed class ScoreSaberMetadataDto
{
    [JsonProperty("page")]
    public int Page { get; set; }

    [JsonProperty("itemsPerPage")]
    public int ItemsPerPage { get; set; }

    [JsonProperty("totalItems")]
    public int TotalItems { get; set; }
}

public sealed class ScoreSaberScoreDto
{
    [JsonProperty("id")]
    public long Id { get; set; }

    [JsonProperty("rank")]
    public int Rank { get; set; }

    [JsonProperty("unmodifiedScore")]
    public int? UnmodifiedScore { get; set; }

    [JsonProperty("modifiedScore")]
    public int ModifiedScore { get; set; }

    [JsonProperty("accuracy")]
    public double? Accuracy { get; set; }

    [JsonProperty("pp")]
    public double? Pp { get; set; }

    [JsonProperty("mods")]
    public List<string>? Mods { get; set; }

    [JsonProperty("player")]
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
    [JsonProperty("id")]
    public string? Id { get; set; }

    [JsonProperty("name")]
    public string? Name { get; set; }

    [JsonProperty("playerNameInGame")]
    public string? PlayerNameInGame { get; set; }

    [JsonProperty("avatar")]
    public string? Avatar { get; set; }

    [JsonProperty("avatarUrl")]
    public string? AvatarUrl { get; set; }

    [JsonProperty("profilePicture")]
    public string? ProfilePicture { get; set; }

    [JsonProperty("profilePictureUrl")]
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
