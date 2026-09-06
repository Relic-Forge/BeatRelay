using System.Threading;
using System.Threading.Tasks;

namespace BeatRelay.BeatLeader;

public interface IBeatLeaderApiClient
{
    string SourceName { get; }

    Task<BeatLeaderApiResult<BeatLeaderOAuthIdentityDto>> GetOAuthIdentityAsync(
        string accessToken,
        CancellationToken cancellationToken);

    Task<BeatLeaderApiResult<LeaderboardScoresResponse>> GetLeaderboardScoresAsync(
        string hash,
        string difficulty,
        string mode,
        int page,
        int count,
        CancellationToken cancellationToken);

    Task<BeatLeaderApiResult<BeatLeaderSongResponse>> GetSongByHashAsync(
        string hash,
        CancellationToken cancellationToken);

    Task<BeatLeaderApiResult<LeaderboardDetailResponse>> GetLeaderboardDetailPageAsync(
        string leaderboardId,
        int page,
        int count,
        CancellationToken cancellationToken);

    Task<BeatLeaderApiResult<BeatLeaderScoreDto>> GetPlayerBestAsync(
        string leaderboardContext,
        string playerId,
        string hash,
        string difficulty,
        string mode,
        CancellationToken cancellationToken);
}
