using System;
using System.Threading;
using System.Threading.Tasks;
using BeatRelay.BeatLeader;
using BeatRelay.Config;

namespace BeatRelay.Sessions;

public sealed class LeaderboardApiClientSelector : IBeatLeaderApiClient
{
    private readonly OverlayConfig config;
    private readonly IBeatLeaderApiClient beatLeaderClient;
    private readonly IBeatLeaderApiClient scoreSaberClient;

    public LeaderboardApiClientSelector(
        OverlayConfig config,
        IBeatLeaderApiClient beatLeaderClient,
        IBeatLeaderApiClient scoreSaberClient)
    {
        this.config = config ?? throw new ArgumentNullException(nameof(config));
        this.beatLeaderClient = beatLeaderClient ?? throw new ArgumentNullException(nameof(beatLeaderClient));
        this.scoreSaberClient = scoreSaberClient ?? throw new ArgumentNullException(nameof(scoreSaberClient));
    }

    public string SourceName => ActiveClient.SourceName;

    private IBeatLeaderApiClient ActiveClient =>
        string.Equals(config.LeaderboardSource, "ScoreSaber", StringComparison.OrdinalIgnoreCase)
            ? scoreSaberClient
            : beatLeaderClient;

    public Task<BeatLeaderApiResult<BeatLeaderOAuthIdentityDto>> GetOAuthIdentityAsync(string accessToken, CancellationToken cancellationToken)
        => ActiveClient.GetOAuthIdentityAsync(accessToken, cancellationToken);

    public Task<BeatLeaderApiResult<LeaderboardScoresResponse>> GetLeaderboardScoresAsync(
        string hash,
        string difficulty,
        string mode,
        int page,
        int count,
        CancellationToken cancellationToken)
        => ActiveClient.GetLeaderboardScoresAsync(hash, difficulty, mode, page, count, cancellationToken);

    public Task<BeatLeaderApiResult<BeatLeaderSongResponse>> GetSongByHashAsync(string hash, CancellationToken cancellationToken)
        => ActiveClient.GetSongByHashAsync(hash, cancellationToken);

    public Task<BeatLeaderApiResult<LeaderboardDetailResponse>> GetLeaderboardDetailPageAsync(
        string leaderboardId,
        int page,
        int count,
        CancellationToken cancellationToken)
        => ActiveClient.GetLeaderboardDetailPageAsync(leaderboardId, page, count, cancellationToken);

    public Task<BeatLeaderApiResult<BeatLeaderScoreDto>> GetPlayerBestAsync(
        string leaderboardContext,
        string playerId,
        string hash,
        string difficulty,
        string mode,
        CancellationToken cancellationToken)
        => ActiveClient.GetPlayerBestAsync(leaderboardContext, playerId, hash, difficulty, mode, cancellationToken);
}
