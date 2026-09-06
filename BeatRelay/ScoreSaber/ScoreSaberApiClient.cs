using System;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BeatRelay.BeatLeader;
using BeatRelay.Diagnostics;

namespace BeatRelay.ScoreSaber;

public sealed class ScoreSaberApiClient : IBeatLeaderApiClient
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient httpClient;
    private readonly BeatLeaderRateLimiter rateLimiter;
    private readonly ScoreSaberEndpointBuilder endpoints;
    private readonly IOverlayLogger logger;
    private readonly object sessionSync = new();
    private string lastHash = string.Empty;
    private string lastDifficulty = string.Empty;
    private string lastMode = string.Empty;
    private string lastLeaderboardId = string.Empty;
    private int? lastRealmId;
    private ScoreSaberLeaderboardResponse? lastLeaderboardInfo;
    private ScoreSaberScoreDto? lastPlayerScore;

    public ScoreSaberApiClient(HttpClient httpClient, BeatLeaderRateLimiter rateLimiter, IOverlayLogger logger)
        : this(httpClient, rateLimiter, new ScoreSaberEndpointBuilder(), logger)
    {
    }

    public ScoreSaberApiClient(
        HttpClient httpClient,
        BeatLeaderRateLimiter rateLimiter,
        ScoreSaberEndpointBuilder endpoints,
        IOverlayLogger logger)
    {
        this.httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        this.rateLimiter = rateLimiter ?? throw new ArgumentNullException(nameof(rateLimiter));
        this.endpoints = endpoints ?? throw new ArgumentNullException(nameof(endpoints));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public string SourceName => "ScoreSaber";

    public Task<BeatLeaderApiResult<BeatLeaderOAuthIdentityDto>> GetOAuthIdentityAsync(string accessToken, CancellationToken cancellationToken)
    {
        return Task.FromResult(BeatLeaderApiResult<BeatLeaderOAuthIdentityDto>.Failure(HttpStatusCode.NotFound, "ScoreSaber OAuth identity is not configured for BeatRelay."));
    }

    public async Task<BeatLeaderApiResult<LeaderboardScoresResponse>> GetLeaderboardScoresAsync(
        string hash,
        string difficulty,
        string mode,
        int page,
        int count,
        CancellationToken cancellationToken)
    {
        var infoResult = await GetLeaderboardInfoAsync(hash, difficulty, mode, cancellationToken).ConfigureAwait(false);
        if (!infoResult.IsSuccess || infoResult.Value == null)
        {
            return BeatLeaderApiResult<LeaderboardScoresResponse>.Failure(infoResult.StatusCode, infoResult.ErrorMessage ?? "ScoreSaber leaderboard unavailable.");
        }

        var realmId = infoResult.Value.Realm?.RealmId;
        var scoresResult = await GetJsonAsync<ScoreSaberScoresResponse>(
            endpoints.BuildLeaderboardScoresUri(hash, difficulty, mode, page, count, realmId),
            "scoresaber_leaderboard_scores",
            TimeSpan.FromSeconds(4),
            cancellationToken).ConfigureAwait(false);
        if (!scoresResult.IsSuccess || scoresResult.Value == null)
        {
            return BeatLeaderApiResult<LeaderboardScoresResponse>.Failure(scoresResult.StatusCode, scoresResult.ErrorMessage ?? "ScoreSaber scores unavailable.");
        }

        RememberSession(hash, difficulty, mode, infoResult.Value, scoresResult.Value.PlayerScore);
        var mapped = ScoreSaberResponseMapper.ToLeaderboardScoresResponse(hash, difficulty, mode, infoResult.Value, scoresResult.Value);
        return BeatLeaderApiResult<LeaderboardScoresResponse>.Success(mapped, scoresResult.StatusCode ?? HttpStatusCode.OK);
    }

    public Task<BeatLeaderApiResult<BeatLeaderSongResponse>> GetSongByHashAsync(string hash, CancellationToken cancellationToken)
    {
        return Task.FromResult(BeatLeaderApiResult<BeatLeaderSongResponse>.Failure(HttpStatusCode.NotFound, "ScoreSaber does not use BeatLeader song difficulty ratings."));
    }

    public Task<BeatLeaderApiResult<LeaderboardDetailResponse>> GetLeaderboardDetailPageAsync(
        string leaderboardId,
        int page,
        int count,
        CancellationToken cancellationToken)
    {
        return Task.FromResult(BeatLeaderApiResult<LeaderboardDetailResponse>.Failure(HttpStatusCode.NotFound, "ScoreSaber scores already include player avatar data."));
    }

    public async Task<BeatLeaderApiResult<BeatLeaderScoreDto>> GetPlayerBestAsync(
        string leaderboardContext,
        string playerId,
        string hash,
        string difficulty,
        string mode,
        CancellationToken cancellationToken)
    {
        var cachedPlayerScore = ResolveCachedPlayerScore(hash, difficulty, mode, playerId);
        if (cachedPlayerScore != null)
        {
            return BeatLeaderApiResult<BeatLeaderScoreDto>.Success(cachedPlayerScore.ToBeatLeaderScoreDto(), HttpStatusCode.OK);
        }

        var leaderboardId = ResolveCachedLeaderboardId(hash, difficulty, mode);
        if (string.IsNullOrWhiteSpace(leaderboardId))
        {
            var info = await GetLeaderboardInfoAsync(hash, difficulty, mode, cancellationToken).ConfigureAwait(false);
            if (!info.IsSuccess || info.Value == null)
            {
                return BeatLeaderApiResult<BeatLeaderScoreDto>.Failure(info.StatusCode, info.ErrorMessage ?? "ScoreSaber leaderboard unavailable.");
            }

            leaderboardId = info.Value.Id.ToString(CultureInfo.InvariantCulture);
            RememberSession(hash, difficulty, mode, info.Value);
        }

        var realmId = ResolveCachedRealm(hash, difficulty, mode);
        var result = await GetJsonAsync<ScoreSaberPlayerScoresResponse>(
            endpoints.BuildPlayerScoresUri(playerId, leaderboardId!, limit: 1, realmId),
            "scoresaber_player_best",
            TimeSpan.FromSeconds(3),
            cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess || result.Value == null || result.Value.Data.Count == 0 || result.Value.Data[0].Score == null)
        {
            return BeatLeaderApiResult<BeatLeaderScoreDto>.Failure(result.StatusCode, result.ErrorMessage ?? "ScoreSaber player best unavailable.");
        }

        RememberPlayerScore(hash, difficulty, mode, result.Value.Data[0].Score);
        return BeatLeaderApiResult<BeatLeaderScoreDto>.Success(result.Value.Data[0].Score!.ToBeatLeaderScoreDto(), result.StatusCode ?? HttpStatusCode.OK);
    }

    private async Task<BeatLeaderApiResult<T>> GetJsonAsync<T>(
        Uri uri,
        string eventName,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var result = await GetStringAsync(uri, eventName, timeout, cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess || string.IsNullOrWhiteSpace(result.Value))
        {
            return BeatLeaderApiResult<T>.Failure(result.StatusCode, result.ErrorMessage ?? "Request failed.");
        }

        try
        {
            var value = JsonSerializer.Deserialize<T>(result.Value!, SerializerOptions);
            return value == null
                ? BeatLeaderApiResult<T>.Failure(result.StatusCode, "ScoreSaber response was empty.")
                : BeatLeaderApiResult<T>.Success(value, result.StatusCode ?? HttpStatusCode.OK);
        }
        catch (JsonException ex)
        {
            logger.Warn($"{eventName}_deserialize_failed", ex.Message);
            return BeatLeaderApiResult<T>.Failure(result.StatusCode, "ScoreSaber response could not be parsed.");
        }
    }

    private async Task<BeatLeaderApiResult<ScoreSaberLeaderboardResponse>> GetLeaderboardInfoAsync(
        string hash,
        string difficulty,
        string mode,
        CancellationToken cancellationToken)
    {
        var cached = ResolveCachedLeaderboardInfo(hash, difficulty, mode);
        if (cached != null)
        {
            return BeatLeaderApiResult<ScoreSaberLeaderboardResponse>.Success(cached, HttpStatusCode.OK);
        }

        var result = await GetJsonAsync<ScoreSaberLeaderboardResponse>(
            endpoints.BuildLeaderboardInfoUri(hash, difficulty, mode),
            "scoresaber_leaderboard_info",
            TimeSpan.FromSeconds(4),
            cancellationToken).ConfigureAwait(false);
        if (result.IsSuccess && result.Value != null)
        {
            RememberSession(hash, difficulty, mode, result.Value);
        }

        return result;
    }

    private async Task<BeatLeaderApiResult<string>> GetStringAsync(
        Uri uri,
        string eventName,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        try
        {
            await rateLimiter.WaitAsync(cancellationToken).ConfigureAwait(false);

            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(timeout);
            using var response = await httpClient.GetAsync(uri, timeoutSource.Token).ConfigureAwait(false);
            var content = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                logger.Warn($"{eventName}_http_failed", $"ScoreSaber returned {(int)response.StatusCode} for {uri.PathAndQuery}.");
                return BeatLeaderApiResult<string>.Failure(response.StatusCode, content);
            }

            return BeatLeaderApiResult<string>.Success(content, response.StatusCode);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.Warn($"{eventName}_timeout", $"ScoreSaber request timed out for {uri.PathAndQuery}.");
            return BeatLeaderApiResult<string>.Failure(null, "ScoreSaber request timed out.");
        }
        catch (HttpRequestException ex)
        {
            logger.Warn($"{eventName}_request_failed", ex.Message);
            return BeatLeaderApiResult<string>.Failure(null, ex.Message);
        }
    }

    private int? ResolveCachedRealm(string hash, string difficulty, string mode)
    {
        lock (sessionSync)
        {
            return IsCachedSession(hash, difficulty, mode) ? lastRealmId : null;
        }
    }

    private ScoreSaberLeaderboardResponse? ResolveCachedLeaderboardInfo(string hash, string difficulty, string mode)
    {
        lock (sessionSync)
        {
            return IsCachedSession(hash, difficulty, mode) ? lastLeaderboardInfo : null;
        }
    }

    private string ResolveCachedLeaderboardId(string hash, string difficulty, string mode)
    {
        lock (sessionSync)
        {
            return IsCachedSession(hash, difficulty, mode) ? lastLeaderboardId : string.Empty;
        }
    }

    private ScoreSaberScoreDto? ResolveCachedPlayerScore(string hash, string difficulty, string mode, string playerId)
    {
        lock (sessionSync)
        {
            if (!IsCachedSession(hash, difficulty, mode)
                || lastPlayerScore == null
                || lastPlayerScore.Rank <= 0
                || lastPlayerScore.ModifiedScore <= 0
                || !IsSamePlayer(lastPlayerScore.Player, playerId))
            {
                return null;
            }

            return lastPlayerScore;
        }
    }

    private void RememberSession(
        string hash,
        string difficulty,
        string mode,
        ScoreSaberLeaderboardResponse leaderboardInfo,
        ScoreSaberScoreDto? playerScore = null)
    {
        lock (sessionSync)
        {
            var sameSession = IsCachedSession(hash, difficulty, mode);
            lastHash = Normalize(hash);
            lastDifficulty = Normalize(difficulty);
            lastMode = Normalize(mode);
            lastLeaderboardId = leaderboardInfo.Id.ToString(CultureInfo.InvariantCulture);
            lastRealmId = leaderboardInfo.Realm?.RealmId;
            lastLeaderboardInfo = leaderboardInfo;
            if (!sameSession)
            {
                lastPlayerScore = null;
            }

            if (playerScore != null)
            {
                lastPlayerScore = playerScore;
            }
        }
    }

    private void RememberPlayerScore(string hash, string difficulty, string mode, ScoreSaberScoreDto? playerScore)
    {
        if (playerScore == null)
        {
            return;
        }

        lock (sessionSync)
        {
            if (IsCachedSession(hash, difficulty, mode))
            {
                lastPlayerScore = playerScore;
            }
        }
    }

    private bool IsCachedSession(string hash, string difficulty, string mode)
    {
        return string.Equals(lastHash, Normalize(hash), StringComparison.OrdinalIgnoreCase)
            && string.Equals(lastDifficulty, Normalize(difficulty), StringComparison.OrdinalIgnoreCase)
            && string.Equals(lastMode, Normalize(mode), StringComparison.OrdinalIgnoreCase);
    }

    private static string Normalize(string value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : value.Trim().ToLowerInvariant();
    }

    private static bool IsSamePlayer(ScoreSaberPlayerDto? player, string playerId)
    {
        var candidateId = player?.Id;
        if (string.IsNullOrWhiteSpace(candidateId) || string.IsNullOrWhiteSpace(playerId))
        {
            return false;
        }

        return string.Equals(candidateId!.Trim(), playerId.Trim(), StringComparison.OrdinalIgnoreCase);
    }
}
