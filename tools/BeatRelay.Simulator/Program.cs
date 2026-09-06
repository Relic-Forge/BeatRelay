using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using BeatRelay.BeatLeader;
using BeatRelay.BeatSaber;
using BeatRelay.Config;
using BeatRelay.Diagnostics;
using BeatRelay.Sessions;
using BeatRelay.UI;

var options = SimulationOptions.Parse(args);
var scenario = options.HasFileInputs
    ? LoadScenario(options)
    : BuildDefaultScenario();

var apiClient = new SimulatedBeatLeaderApiClient(scenario.Leaderboard);
var stateMachine = new OverlayStateMachine(new OverlayConfig
{
    NoteGapSeconds = scenario.NoteGapSeconds,
});
var sessionManager = new BeatLeaderOverlaySessionManager(apiClient, stateMachine, new ConsoleOverlayLogger());

var start = await sessionManager.StartAsync(
    scenario.Beatmap,
    playerId: scenario.PlayerId,
    CancellationToken.None);

if (!start.IsStarted)
{
    Console.WriteLine(start.ErrorMessage);
    return;
}

Console.WriteLine("BeatRelay Simulation");
Console.WriteLine("Scenario: " + scenario.Name);
Console.WriteLine();
Console.WriteLine("Time  Progress  Mode         Rank                  Message                         Rows");
Console.WriteLine("----  --------  -----------  --------------------  ------------------------------  ------------------------------");

foreach (var tick in scenario.Ticks)
{
    var result = sessionManager.Update(tick.RunState);
    PrintTick(tick, result);
    await sessionManager.TryFetchSuggestedPageAsync(result, CancellationToken.None);
}

static SimulationScenario LoadScenario(SimulationOptions options)
{
    if (options.LeaderboardPath == null || options.TicksPath == null)
    {
        throw new InvalidOperationException("Both --leaderboard and --ticks are required for file-backed simulation.");
    }

    var leaderboard = ReadJson<LeaderboardScoresResponse>(options.LeaderboardPath);
    var tickFile = ReadJson<SimulationTickFile>(options.TicksPath);

    return new SimulationScenario(
        tickFile.Name,
        tickFile.Beatmap,
        tickFile.PlayerId,
        tickFile.NoteGapSeconds <= 0 ? 3.0 : tickFile.NoteGapSeconds,
        leaderboard,
        tickFile.Ticks.Select(tick => new SimulationTick(tick.ElapsedSeconds, tick.ToRunState())).ToList());
}

static T ReadJson<T>(string path)
{
    var json = File.ReadAllText(path);
    return JsonSerializer.Deserialize<T>(json, new JsonSerializerOptions
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    }) ?? throw new InvalidOperationException("Could not parse " + path);
}

static SimulationScenario BuildDefaultScenario()
{
    return new SimulationScenario(
        "ranked ExpertPlus / Standard map, local score improves late",
        new BeatmapSessionInfo
        {
            Hash = "ABCDEF123",
            Difficulty = "ExpertPlus",
            Mode = "Standard",
            SongName = "Simulated Map",
            MapperName = "Codex"
        },
        PlayerId: null,
        NoteGapSeconds: 3.0,
        BuildRankedLeaderboardResponse(),
        BuildDefaultTicks());
}

static IReadOnlyList<SimulationTick> BuildDefaultTicks()
{
    return new List<SimulationTick>
    {
        Tick(0, 0.01, 8_900, 5, false, true, 0),
        Tick(8, 0.06, 53_400, 45, false, true, 0),
        Tick(16, 0.13, 115_700, 95, false, true, 0),
        Tick(18, 0.15, 133_500, 110, false, true, 0),
        Tick(30, 0.25, 222_500, 190, false, true, 0),
        Tick(45, 0.38, 338_200, 280, true, false, 4.2),
        Tick(55, 0.46, 409_400, 350, false, true, 0),
        Tick(70, 0.58, 539_400, 450, false, false, 3.5),
        Tick(84, 0.70, 651_000, 555, false, true, 0),
        Tick(96, 0.80, 768_000, 640, false, true, 0),
        Tick(108, 0.90, 864_000, 735, false, true, 0),
        Tick(118, 0.98, 940_800, 820, false, true, 0),
    };
}

static SimulationTick Tick(
    int elapsedSeconds,
    double progress,
    int currentModifiedScore,
    int scoredNotes,
    bool paused,
    bool hasRecentNotes,
    double secondsSinceLastNote)
{
    return new SimulationTick(
        elapsedSeconds,
        new BeatmapRunState
        {
            CurrentScore = currentModifiedScore,
            CurrentModifiedScore = currentModifiedScore,
            Accuracy = progress <= 0 ? 0 : Math.Min(1.0, currentModifiedScore / (990_000.0 * progress)),
            Combo = scoredNotes,
            ScoredNotes = scoredNotes,
            SongProgressRatio = progress,
            IsPaused = paused,
            HasRecentNotes = hasRecentNotes,
            SecondsSinceLastNote = secondsSinceLastNote,
            KnownMaxModifiedScore = 1_000_000
        });
}

static void PrintTick(SimulationTick tick, OverlayUpdateResult result)
{
    var view = result.ViewModel;
    var rank = string.IsNullOrWhiteSpace(view.MovementText)
        ? view.RankText
        : $"{view.RankText} ({view.MovementText})";
    var rows = string.Join(" | ", view.Rows.Take(5).Select(row =>
        row.IsLocalPlayer
            ? $"#{row.Rank} You {row.ValueText}"
            : $"#{row.Rank} {row.PlayerName} {row.ValueText}"));

    if (result.PageToFetch.HasValue)
    {
        rows = string.IsNullOrWhiteSpace(rows)
            ? $"fetch page {result.PageToFetch.Value}"
            : $"{rows} | fetch page {result.PageToFetch.Value}";
    }

    Console.WriteLine(
        string.Create(
            CultureInfo.InvariantCulture,
            $"{tick.ElapsedSeconds,4}s  {tick.RunState.SongProgressRatio,7:P0}  {view.Mode,-11}  {Trim(rank, 20),-20}  {Trim(view.MessageText, 30),-30}  {Trim(rows, 30)}"));
}

static string Trim(string value, int maxLength)
{
    if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
    {
        return value;
    }

    return value[..Math.Max(0, maxLength - 1)] + "...";
}

static LeaderboardScoresResponse BuildRankedLeaderboardResponse()
{
    var response = new LeaderboardScoresResponse
    {
        Metadata = new LeaderboardMetadata
        {
            Page = 1,
            ItemsPerPage = 10,
            Total = 10
        },
        Container = new LeaderboardContainer
        {
            LeaderboardId = "sim-ranked-leaderboard",
            Ranked = true
        }
    };

    var scores = new[]
    {
        (1, "Aster", 990_000, 520.2),
        (2, "Nova", 975_000, 505.8),
        (3, "Flux", 960_000, 492.4),
        (4, "Rift", 945_000, 481.1),
        (5, "Echo", 930_000, 470.6),
        (6, "Pulse", 915_000, 458.3),
        (7, "Vector", 900_000, 447.9),
        (8, "Orbit", 885_000, 436.2),
        (9, "Kite", 870_000, 425.7),
        (10, "Mira", 850_000, 412.0)
    };

    foreach (var (rank, name, score, pp) in scores)
    {
        response.Data.Add(new BeatLeaderScoreDto
        {
            Id = rank,
            Rank = rank,
            ModifiedScore = score,
            Pp = pp,
            Player = new BeatLeaderPlayerDto
            {
                Id = "player-" + rank.ToString(CultureInfo.InvariantCulture),
                Name = name
            }
        });
    }

    return response;
}

internal sealed record SimulationScenario(
    string Name,
    BeatmapSessionInfo Beatmap,
    string? PlayerId,
    double NoteGapSeconds,
    LeaderboardScoresResponse Leaderboard,
    IReadOnlyList<SimulationTick> Ticks);

internal sealed record SimulationTick(int ElapsedSeconds, BeatmapRunState RunState);

internal sealed class SimulationTickFile
{
    public string Name { get; set; } = "file-backed scenario";

    public BeatmapSessionInfo Beatmap { get; set; } = new();

    public string? PlayerId { get; set; }


    public double NoteGapSeconds { get; set; } = 3.0;

    public List<SimulationTickDto> Ticks { get; set; } = new();
}

internal sealed class SimulationTickDto
{
    public int ElapsedSeconds { get; set; }

    public int CurrentModifiedScore { get; set; }

    public int CurrentScore { get; set; }

    public double Accuracy { get; set; }

    public int Combo { get; set; }

    public int MissCount { get; set; }

    public int BadCutCount { get; set; }

    public int ScoredNotes { get; set; }

    public int? KnownMaxModifiedScore { get; set; }

    public double SongProgressRatio { get; set; }

    public bool IsPaused { get; set; }

    public bool HasRecentNotes { get; set; } = true;

    public double SecondsSinceLastNote { get; set; }

    public List<string> ActiveModifiers { get; set; } = new();

    public BeatmapRunState ToRunState()
    {
        return new BeatmapRunState
        {
            CurrentScore = CurrentScore > 0 ? CurrentScore : CurrentModifiedScore,
            CurrentModifiedScore = CurrentModifiedScore,
            Accuracy = Accuracy,
            Combo = Combo,
            MissCount = MissCount,
            BadCutCount = BadCutCount,
            ScoredNotes = ScoredNotes,
            KnownMaxModifiedScore = KnownMaxModifiedScore,
            SongProgressRatio = SongProgressRatio,
            IsPaused = IsPaused,
            HasRecentNotes = HasRecentNotes,
            SecondsSinceLastNote = SecondsSinceLastNote,
            ActiveModifiers = ActiveModifiers
        };
    }
}

internal sealed class SimulationOptions
{
    public string? LeaderboardPath { get; private set; }

    public string? TicksPath { get; private set; }

    public bool HasFileInputs => LeaderboardPath != null || TicksPath != null;

    public static SimulationOptions Parse(string[] args)
    {
        var options = new SimulationOptions();
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--leaderboard" && i + 1 < args.Length)
            {
                options.LeaderboardPath = args[++i];
            }
            else if (args[i] == "--ticks" && i + 1 < args.Length)
            {
                options.TicksPath = args[++i];
            }
        }

        return options;
    }
}

internal sealed class SimulatedBeatLeaderApiClient : IBeatLeaderApiClient
{
    private readonly LeaderboardScoresResponse leaderboardResponse;

    public string SourceName => "BeatLeader";

    public SimulatedBeatLeaderApiClient(LeaderboardScoresResponse leaderboardResponse)
    {
        this.leaderboardResponse = leaderboardResponse;
    }

    public Task<BeatLeaderApiResult<BeatLeaderOAuthIdentityDto>> GetOAuthIdentityAsync(
        string accessToken,
        CancellationToken cancellationToken)
    {
        return Task.FromResult(BeatLeaderApiResult<BeatLeaderOAuthIdentityDto>.Failure(System.Net.HttpStatusCode.NotFound, "No simulated identity."));
    }

    public Task<BeatLeaderApiResult<LeaderboardScoresResponse>> GetLeaderboardScoresAsync(
        string hash,
        string difficulty,
        string mode,
        int page,
        int count,
        CancellationToken cancellationToken)
    {
        return Task.FromResult(page == 1
            ? BeatLeaderApiResult<LeaderboardScoresResponse>.Success(leaderboardResponse, System.Net.HttpStatusCode.OK)
            : BeatLeaderApiResult<LeaderboardScoresResponse>.Failure(System.Net.HttpStatusCode.NotFound, "No simulated page."));
    }

    public Task<BeatLeaderApiResult<BeatLeaderScoreDto>> GetPlayerBestAsync(
        string leaderboardContext,
        string playerId,
        string hash,
        string difficulty,
        string mode,
        CancellationToken cancellationToken)
    {
        return Task.FromResult(BeatLeaderApiResult<BeatLeaderScoreDto>.Failure(System.Net.HttpStatusCode.NotFound, "No simulated PB."));
    }

    public Task<BeatLeaderApiResult<BeatLeaderSongResponse>> GetSongByHashAsync(
        string hash,
        CancellationToken cancellationToken)
    {
        return Task.FromResult(BeatLeaderApiResult<BeatLeaderSongResponse>.Failure(System.Net.HttpStatusCode.NotFound, "No simulated song."));
    }

    public Task<BeatLeaderApiResult<LeaderboardDetailResponse>> GetLeaderboardDetailPageAsync(
        string leaderboardId,
        int page,
        int count,
        CancellationToken cancellationToken)
    {
        return Task.FromResult(BeatLeaderApiResult<LeaderboardDetailResponse>.Failure(System.Net.HttpStatusCode.NotFound, "No simulated leaderboard detail."));
    }

}

internal sealed class ConsoleOverlayLogger : IOverlayLogger
{
    public void Info(string eventName, string message)
    {
    }

    public void Warn(string eventName, string message)
    {
        Console.WriteLine($"warn: {eventName}: {message}");
    }

    public void Error(string eventName, string message)
    {
        Console.WriteLine($"error: {eventName}: {message}");
    }
}
