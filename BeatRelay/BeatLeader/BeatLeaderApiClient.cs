using System;
using System.Linq;
using System.Net.Http.Headers;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BeatRelay.Diagnostics;

namespace BeatRelay.BeatLeader;

public sealed class BeatLeaderApiClient : IBeatLeaderApiClient
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient httpClient;
    private readonly BeatLeaderRateLimiter rateLimiter;
    private readonly BeatLeaderEndpointBuilder endpoints;
    private readonly IOverlayLogger logger;

    public string SourceName => "BeatLeader";

    public BeatLeaderApiClient(
        HttpClient httpClient,
        BeatLeaderRateLimiter rateLimiter,
        IOverlayLogger logger)
        : this(httpClient, rateLimiter, new BeatLeaderEndpointBuilder(), logger)
    {
    }

    public BeatLeaderApiClient(
        HttpClient httpClient,
        BeatLeaderRateLimiter rateLimiter,
        BeatLeaderEndpointBuilder endpoints,
        IOverlayLogger logger)
    {
        this.httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        this.rateLimiter = rateLimiter ?? throw new ArgumentNullException(nameof(rateLimiter));
        this.endpoints = endpoints ?? throw new ArgumentNullException(nameof(endpoints));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public Task<BeatLeaderApiResult<LeaderboardScoresResponse>> GetLeaderboardScoresAsync(
        string hash,
        string difficulty,
        string mode,
        int page,
        int count,
        CancellationToken cancellationToken)
    {
        var uri = endpoints.BuildLeaderboardScoresUri(hash, difficulty, mode, page, count);
        return GetLeaderboardScoresJsonAsync(uri, hash, difficulty, mode, "leaderboard_scores", TimeSpan.FromSeconds(4), cancellationToken);
    }

    private async Task<BeatLeaderApiResult<LeaderboardScoresResponse>> GetLeaderboardScoresJsonAsync(
        Uri uri,
        string hash,
        string difficulty,
        string mode,
        string eventName,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var result = await GetJsonAsync<LeaderboardScoresResponse>(uri, eventName, timeout, cancellationToken).ConfigureAwait(false);
        if (result.IsSuccess && result.Value != null)
        {
            NormalizeLeaderboardScoresResponse(result.Value, hash, difficulty, mode);
        }

        return result;
    }

    private static void NormalizeLeaderboardScoresResponse(LeaderboardScoresResponse response, string hash, string difficulty, string mode)
    {
        response.Container ??= new LeaderboardContainer();
        if (string.IsNullOrWhiteSpace(response.Container.LeaderboardId))
        {
            response.Container.LeaderboardId = response.Data
                .FirstOrDefault(score => !string.IsNullOrWhiteSpace(score.LeaderboardId))
                ?.LeaderboardId;
        }

        if (string.IsNullOrWhiteSpace(response.Container.LeaderboardId))
        {
            response.Container.LeaderboardId = $"{hash.Trim().ToLowerInvariant()}:{difficulty.Trim()}:{mode.Trim()}";
        }

        response.Container.SourceName = "BeatLeader";
        response.Container.RankByPp ??= true;
        response.Container.SupportsPp ??= true;
        if (!response.Container.MaxScore.HasValue)
        {
            response.Container.MaxScore = response.Data
                .Where(score => score.BaseScore.GetValueOrDefault() > 0 && score.Accuracy.GetValueOrDefault() > 0)
                .Select(score => (int?)Math.Max(0, (int)Math.Round(score.BaseScore!.Value / score.Accuracy!.Value, MidpointRounding.AwayFromZero)))
                .FirstOrDefault();
        }
    }

    public Task<BeatLeaderApiResult<BeatLeaderOAuthIdentityDto>> GetOAuthIdentityAsync(
        string accessToken,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            return Task.FromResult(BeatLeaderApiResult<BeatLeaderOAuthIdentityDto>.Failure(null, "BeatLeader access token is required."));
        }

        var uri = endpoints.BuildOAuthIdentityUri();
        return GetJsonWithBearerAsync<BeatLeaderOAuthIdentityDto>(uri, accessToken.Trim(), "oauth_identity", TimeSpan.FromSeconds(4), cancellationToken);
    }

    public Task<BeatLeaderApiResult<BeatLeaderSongResponse>> GetSongByHashAsync(
        string hash,
        CancellationToken cancellationToken)
    {
        var uri = endpoints.BuildSongByHashUri(hash);
        return GetJsonAsync<BeatLeaderSongResponse>(uri, "song_by_hash", TimeSpan.FromSeconds(4), cancellationToken);
    }

    public Task<BeatLeaderApiResult<LeaderboardDetailResponse>> GetLeaderboardDetailPageAsync(
        string leaderboardId,
        int page,
        int count,
        CancellationToken cancellationToken)
    {
        var uri = endpoints.BuildLeaderboardDetailUri(leaderboardId, page, count);
        return GetJsonAsync<LeaderboardDetailResponse>(uri, "leaderboard_detail", TimeSpan.FromSeconds(4), cancellationToken);
    }

    public Task<BeatLeaderApiResult<BeatLeaderScoreDto>> GetPlayerBestAsync(
        string leaderboardContext,
        string playerId,
        string hash,
        string difficulty,
        string mode,
        CancellationToken cancellationToken)
    {
        var uri = endpoints.BuildPlayerBestUri(leaderboardContext, playerId, hash, difficulty, mode);
        return GetJsonAsync<BeatLeaderScoreDto>(uri, "player_best", TimeSpan.FromSeconds(3), cancellationToken);
    }

    private async Task<BeatLeaderApiResult<T>> GetJsonAsync<T>(
        Uri uri,
        string eventName,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var result = await GetStringAsync(uri, eventName, timeout, cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess || result.Value == null)
        {
            return BeatLeaderApiResult<T>.Failure(result.StatusCode, result.ErrorMessage ?? "Request failed.");
        }

        try
        {
            var value = JsonSerializer.Deserialize<T>(result.Value, SerializerOptions);
            return value == null
                ? BeatLeaderApiResult<T>.Failure(result.StatusCode, "BeatLeader response was empty.")
                : BeatLeaderApiResult<T>.Success(value, result.StatusCode!.Value);
        }
        catch (JsonException ex)
        {
            logger.Warn($"{eventName}_deserialize_failed", ex.Message);
            return BeatLeaderApiResult<T>.Failure(result.StatusCode, "BeatLeader response could not be parsed.");
        }
    }

    private async Task<BeatLeaderApiResult<T>> GetJsonWithBearerAsync<T>(
        Uri uri,
        string accessToken,
        string eventName,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var result = await GetStringWithBearerAsync(uri, accessToken, eventName, timeout, cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess || result.Value == null)
        {
            return BeatLeaderApiResult<T>.Failure(result.StatusCode, result.ErrorMessage ?? "Request failed.");
        }

        try
        {
            var value = JsonSerializer.Deserialize<T>(result.Value, SerializerOptions);
            return value == null
                ? BeatLeaderApiResult<T>.Failure(result.StatusCode, "BeatLeader response was empty.")
                : BeatLeaderApiResult<T>.Success(value, result.StatusCode!.Value);
        }
        catch (JsonException ex)
        {
            logger.Warn($"{eventName}_deserialize_failed", ex.Message);
            return BeatLeaderApiResult<T>.Failure(result.StatusCode, "BeatLeader response could not be parsed.");
        }
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
                logger.Warn($"{eventName}_http_failed", $"BeatLeader returned {(int)response.StatusCode} for {uri.PathAndQuery}.");
                return BeatLeaderApiResult<string>.Failure(response.StatusCode, content);
            }

            return BeatLeaderApiResult<string>.Success(content, response.StatusCode);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.Warn($"{eventName}_timeout", $"BeatLeader request timed out for {uri.PathAndQuery}.");
            return BeatLeaderApiResult<string>.Failure(null, "BeatLeader request timed out.");
        }
        catch (HttpRequestException ex)
        {
            logger.Warn($"{eventName}_request_failed", ex.Message);
            return BeatLeaderApiResult<string>.Failure(null, ex.Message);
        }
    }

    private async Task<BeatLeaderApiResult<string>> GetStringWithBearerAsync(
        Uri uri,
        string accessToken,
        string eventName,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        try
        {
            await rateLimiter.WaitAsync(cancellationToken).ConfigureAwait(false);

            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(timeout);
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            using var response = await httpClient.SendAsync(request, timeoutSource.Token).ConfigureAwait(false);
            var content = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                logger.Warn($"{eventName}_http_failed", $"BeatLeader returned {(int)response.StatusCode} for {uri.PathAndQuery}.");
                return BeatLeaderApiResult<string>.Failure(response.StatusCode, content);
            }

            return BeatLeaderApiResult<string>.Success(content, response.StatusCode);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.Warn($"{eventName}_timeout", $"BeatLeader request timed out for {uri.PathAndQuery}.");
            return BeatLeaderApiResult<string>.Failure(null, "BeatLeader request timed out.");
        }
        catch (HttpRequestException ex)
        {
            logger.Warn($"{eventName}_request_failed", ex.Message);
            return BeatLeaderApiResult<string>.Failure(null, ex.Message);
        }
    }
}
