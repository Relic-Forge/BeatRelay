using BeatRelay;
using BeatRelay.BeatLeader;
using BeatRelay.BeatSaber;
using BeatRelay.Config;
using BeatRelay.Diagnostics;
using BeatRelay.Ranking;
using BeatRelay.ScoreSaber;
using BeatRelay.Sessions;
using BeatRelay.UI;
using System.Net;
using System.Net.Http;
using System.Text.Json;

RunPluginStartupSmokeTest();
RunOverlayConfigDefaultsTest();
RunEndpointBuilderTest();
RunScoreSaberEndpointBuilderTest();
await RunApiClientParsingTestAsync();
RunBeatLeaderNestedSongResponseTest();
await RunScoreSaberApiClientSmartRequestTestAsync();
RunLeaderboardCacheAndProjectionTest();
RunBeatLeaderUnrankedClassicScoreWithoutScoreModifiersTest();
RunBeatLeaderUnrankedClassicScoreWithNonScoringModifiersTest();
RunBeatLeaderUnrankedClassicScoreWithNoFailBeforeFailTest();
RunProjectionAppliesNoFailAfterFailTest();
RunUnrankedModifierProjectionTest();
RunUnrankedPositiveModifierProjectionTest();
RunUnrankedInfersMaxScoreFromRowsTest();
RunUnrankedPositiveModifierUsesClassicFinalScoreTest();
RunBeatLeaderUnrankedDisplaysCurrentScoreWhileRankingClassicFinalScore();
RunUnrankedLeaderboardHidesPpEvenWhenApiClaimsSupport();
RunConfidenceGateTest();
RunRankSmootherTest();
RunOverlayExpansionTransitionTest();
RunOverlayStateMachineTest();
RunFixedPlayerLabelLifecycleTest();
RunReplayAlwaysExpandOverrideTest();
RunOverlayBreakExpansionRulesTest();
RunOverlayTimelineOnlyBreakBehaviorTest();
RunOverlayRememberedOpponentRowsTest();
RunOverlayRawPpModeTest();
RunRankedAlwaysDisplaysPpTest();
RunBeatLeaderPpRowsPromoteRankedDisplayTest();
RunRankedPpComparisonDoesNotUnderrankAfterEarlyGateTest();
RunBeatLeaderCurveUsesFetchedRatingsTest();
RunOverlayRangeFetchHintTest();
RunOverlayMissingVisibleRankRefreshesFetchedPageTest();
RunRankedGapProtectionTest();
RunKnownRankSegmentProjectionTest();
RunBeatLeaderModifierPolicyTest();
RunScoreSaberModifierPolicyTest();
RunScoreSaberPpCurveTest();
RunScoreSaberRankingModeTest();
RunScoreSaberFailedNoFailUsesEffectiveAccuracyTest();
RunScoreSaberAccuracyDoesNotFetchOutsideVisibleWindowTest();
await RunScoreSaberPlayerBestSeedsLocalPageTestAsync();
RunLeaderboardContainerModifierValuesTest();
await RunSessionManagerStartAndPbHintTestAsync();
await RunSessionLifecycleRaceTestsAsync();
RunSessionMainThreadContinuationTest();
await RunReplayUsesLocalLabelTestsAsync();
await RunSessionManagerUsesScorePagesWithoutAvatarDetailFallbackTestAsync();
await RunSessionManagerUnrankedStartsFromBottomTestAsync();
await RunSessionManagerAppliesContainerModifierValuesTestAsync();
await RunSessionManagerSuggestedPageFetchTestAsync();
await RunSessionManagerBackgroundScannerJumpsAfterBroadPageTestAsync();
await RunSessionManagerRefreshesFetchedPageForMissingVisibleRankTestAsync();
RunProjectionUsesBroadScannedSegmentsTest();
await RunRateLimiterTestAsync();

Console.WriteLine("Core smoke tests passed.");

static void RunPluginStartupSmokeTest()
{
    var testRoot = CreateTestRoot();
    var configDirectory = Path.Combine(testRoot, "UserData");
    var logDirectory = Path.Combine(testRoot, "Logs");

    var configManager = new ConfigManager(configDirectory);
    var logger = new OverlayLogger(logDirectory);
    var plugin = new Plugin(configManager, logger);

    plugin.OnApplicationStart();

    Assert(plugin.IsStarted, "Plugin did not enter started state.");
    Assert(File.Exists(configManager.ConfigPath), "Expected config file was not created.");
    Assert(File.Exists(logger.LogPath), "Expected startup log was not created.");

    var startupLog = File.ReadAllText(logger.LogPath);
    Assert(startupLog.Contains("plugin_startup", StringComparison.Ordinal), "Startup log entry was not written.");

    plugin.OnApplicationQuit();
}

static void RunEndpointBuilderTest()
{
    var endpoints = new BeatLeaderEndpointBuilder();
    var uri = endpoints.BuildLeaderboardScoresUri("ABCDEF", "ExpertPlus", "Standard", 2, 50);

    Assert(
        uri.ToString() == "https://api.beatleader.com/v3/scores/abcdef/ExpertPlus/Standard/modifiers/global/page?page=2&count=50",
        $"Unexpected leaderboard URI: {uri}");

}

static async Task RunApiClientParsingTestAsync()
{
    const string responseJson = """
        {
          "metadata": { "page": 1, "itemsPerPage": 1, "total": 1 },
          "container": { "leaderboardId": "leaderboard-1", "ranked": true },
          "data": [
            {
              "id": 10,
              "rank": 1,
              "baseScore": 950000,
              "modifiedScore": 950000,
              "accuracy": 0.95,
              "pp": 400.5,
              "modifiers": "",
              "player": "Player One"
            }
          ]
        }
        """;

    var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StringContent(responseJson)
    });

    var testRoot = CreateTestRoot();
    var client = new BeatLeaderApiClient(
        new HttpClient(handler),
        new BeatLeaderRateLimiter(TimeSpan.Zero, () => DateTimeOffset.UtcNow, Task.Delay),
        new BeatLeaderEndpointBuilder("https://example.test"),
        new OverlayLogger(Path.Combine(testRoot, "Logs")));

    var result = await client.GetLeaderboardScoresAsync("ABCDEF", "ExpertPlus", "Standard", 1, 50, CancellationToken.None);

    Assert(result.IsSuccess, $"Expected API parse success: {result.ErrorMessage}");
    var value = result.Value ?? throw new InvalidOperationException("Expected parsed API value.");
    Assert(value.Container?.LeaderboardId == "leaderboard-1", "Leaderboard ID was not parsed.");
    Assert(value.ToScoreRows()[0].PlayerName == "Player One", "Player name was not mapped.");
    Assert(handler.RequestCount == 1, "Expected one API request.");
    Assert(handler.LastRequestUri?.PathAndQuery == "/v3/scores/abcdef/ExpertPlus/Standard/modifiers/global/page?page=1&count=50", "Unexpected API request path.");
}

static void RunBeatLeaderNestedSongResponseTest()
{
    const string responseJson = """
        {
          "song": {
            "hash": "abcdef",
            "difficulties": [
              {
                "difficultyName": "Expert",
                "modeName": "Standard",
                "status": 3,
                "maxScore": 1149195,
                "stars": 9.955169,
                "predictedAcc": 0.975241,
                "passRating": 10.316791,
                "accRating": 10.143687,
                "techRating": 3.6494896
              }
            ]
          }
        }
        """;

    var parsed = JsonSerializer.Deserialize<BeatLeaderSongResponse>(
        responseJson,
        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
    var difficulties = parsed?.ResolveDifficulties();

    Assert(difficulties != null && difficulties.Count == 1, "Expected nested BeatLeader song difficulties to resolve.");
    var expert = difficulties[0];
    Assert(expert.MaxScore == 1149195, "Expected nested BeatLeader max score to parse.");
    Assert(Math.Abs(expert.AccRating.GetValueOrDefault() - 10.143687d) < 0.000001d, "Expected nested BeatLeader acc rating to parse.");
}

static void RunScoreSaberEndpointBuilderTest()
{
    var endpoints = new ScoreSaberEndpointBuilder();
    var uri = endpoints.BuildLeaderboardScoresUri("ABCDEF", "Expert+", "Standard", 2, 50, realmId: 3);

    Assert(
        uri.ToString() == "https://scoresaber.com/api/v2/leaderboards/hash/abcdef/SoloStandard/9/scores?page=2&limit=50&sort=score&sortDirection=desc",
        $"Unexpected ScoreSaber leaderboard URI: {uri}");


}

static async Task RunScoreSaberApiClientSmartRequestTestAsync()
{
    const string leaderboardInfoJson = """
        {
          "id": 12345,
          "maxScore": 1000000,
          "totalScores": 30,
          "realm": {
            "realmId": 3,
            "realmName": "Global",
            "leaderboardStatus": "RANKED",
            "positiveModifiers": true,
            "stars": 10.5
          }
        }
        """;
    const string firstPageJson = """
        {
          "metadata": { "page": 1, "itemsPerPage": 10, "totalItems": 30 },
          "data": [
            {
              "id": 100,
              "rank": 1,
              "unmodifiedScore": 990000,
              "modifiedScore": 990000,
              "accuracy": 0.99,
              "pp": 600.0,
              "mods": [],
              "player": { "id": "111", "name": "Top Player", "avatar": "/avatars/top.png" }
            }
          ],
          "playerScore": {
            "id": 999,
            "rank": 15,
            "unmodifiedScore": 950000,
            "modifiedScore": 950000,
            "accuracy": 0.95,
            "pp": 450.0,
            "mods": [],
            "player": { "id": "76561198000000000", "name": "Local", "avatar": "/avatars/local.png" }
          }
        }
        """;
    const string secondPageJson = """
        {
          "metadata": { "page": 2, "itemsPerPage": 10, "totalItems": 30 },
          "data": [
            {
              "id": 200,
              "rank": 11,
              "unmodifiedScore": 970000,
              "modifiedScore": 970000,
              "accuracy": 0.97,
              "pp": 500.0,
              "mods": [],
              "player": { "id": "222", "name": "Page Two", "avatar": "/avatars/page-two.png" }
            }
          ],
          "playerScore": null
        }
        """;

    var handler = new FakeHttpMessageHandler(request =>
    {
        var path = request.RequestUri?.PathAndQuery ?? string.Empty;
        var json = path switch
        {
            "/api/v2/leaderboards/hash/abcdef/SoloStandard/9" => leaderboardInfoJson,
            "/api/v2/leaderboards/hash/abcdef/SoloStandard/9/scores?page=1&limit=10&sort=score&sortDirection=desc" => firstPageJson,
            "/api/v2/leaderboards/hash/abcdef/SoloStandard/9/scores?page=2&limit=10&sort=score&sortDirection=desc" => secondPageJson,
            _ => throw new InvalidOperationException("Unexpected ScoreSaber request: " + path)
        };

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json)
        };
    });

    var testRoot = CreateTestRoot();
    var client = new ScoreSaberApiClient(
        new HttpClient(handler),
        new BeatLeaderRateLimiter(TimeSpan.Zero, () => DateTimeOffset.UtcNow, Task.Delay),
        new ScoreSaberEndpointBuilder("https://scoresaber.com"),
        new OverlayLogger(Path.Combine(testRoot, "Logs")));

    var firstPage = await client.GetLeaderboardScoresAsync("ABCDEF", "Expert+", "Standard", 1, 10, CancellationToken.None);
    Assert(firstPage.IsSuccess, $"Expected ScoreSaber first page success: {firstPage.ErrorMessage}");
    Assert(firstPage.Value?.Data.Any(score => score.Player?.Id == "76561198000000000" && score.Rank == 15) == true, "Expected playerScore to be merged into ScoreSaber page data.");

    var playerBest = await client.GetPlayerBestAsync("general", "76561198000000000", "ABCDEF", "Expert+", "Standard", CancellationToken.None);
    Assert(playerBest.IsSuccess, $"Expected cached ScoreSaber player best success: {playerBest.ErrorMessage}");
    Assert(playerBest.Value?.Player?.AvatarUrl == "https://scoresaber.com/avatars/local.png", "Expected cached playerScore avatar to be normalized.");

    var secondPage = await client.GetLeaderboardScoresAsync("ABCDEF", "Expert+", "Standard", 2, 10, CancellationToken.None);
    Assert(secondPage.IsSuccess, $"Expected ScoreSaber second page success: {secondPage.ErrorMessage}");

    Assert(handler.RequestCount == 3, $"Expected info + two score-page requests, got {handler.RequestCount}.");
    Assert(handler.RequestUris[0].PathAndQuery == "/api/v2/leaderboards/hash/abcdef/SoloStandard/9", "Expected first ScoreSaber request to fetch leaderboard info.");
    Assert(!handler.RequestUris[1].PathAndQuery.Contains("realmId", StringComparison.Ordinal), "Anonymous ScoreSaber score requests should not send realmId.");
    Assert(!handler.RequestUris[1].PathAndQuery.Contains("includePlayerScore", StringComparison.Ordinal), "Anonymous ScoreSaber score requests should not request authenticated playerScore data.");
    Assert(handler.RequestUris[2].PathAndQuery.Contains("page=2", StringComparison.Ordinal), "Expected second ScoreSaber page request.");
    Assert(!handler.RequestUris.Skip(1).Any(uri => uri.PathAndQuery.EndsWith("/basic", StringComparison.Ordinal)), "ScoreSaber smart path should not fetch player profiles.");
    Assert(!handler.RequestUris.Skip(1).Any(uri => uri.PathAndQuery.Contains("/players/76561198000000000/scores", StringComparison.Ordinal)), "Cached playerScore should avoid a player scores request.");
}

static void RunOverlayConfigDefaultsTest()
{
    var config = new OverlayConfig();
    Assert(!config.EnableDesktopOverlay, "Desktop overlay default should be off.");
    Assert(config.DynamicPpScore, "Dynamic pp/score default should be on.");
    Assert(Math.Abs(config.ExpandOnBreakSeconds - 3.5) < 0.001, "Expand-on-break default should be 3.5 seconds.");
    Assert(config.VisiblePlayerCount == 2, "Visible players default should be 2.");
    Assert(!config.PreviewExpanded, "Preview mode default should be collapsed.");
    Assert(!config.GetShowBigRank(expanded: false), "Collapsed big rank default should be off.");
    Assert(!config.GetShowBigRank(expanded: true), "Expanded big rank default should be off.");
    Assert(Math.Abs(config.GetPlayerRowScale(expanded: false) - 1.0) < 0.001, "Collapsed player row scale default should be 1.00.");
    Assert(Math.Abs(config.GetPlayerRowScale(expanded: true) - 1.0) < 0.001, "Expanded player row scale default should be 1.00.");
    Assert(Math.Abs(config.GetEffectivePlayerRowScale(expanded: false) - 1.8) < 0.001, "Collapsed player row scale 1.00 should render like the old 0.90.");
    Assert(Math.Abs(config.GetEffectivePlayerRowScale(expanded: true) - 1.8) < 0.001, "Expanded player row scale 1.00 should render like the old 0.90.");
    Assert(Math.Abs(config.InGameOverlayScale - 1.0) < 0.001, "In-game overlay scale default should be 1.00.");
    Assert(Math.Abs(config.GetBackgroundOpacity(expanded: false) - 0.0) < 0.001, "Collapsed background opacity default should be 0%.");
    Assert(Math.Abs(config.GetBackgroundOpacity(expanded: true) - 0.0) < 0.001, "Expanded background opacity default should be 0%.");
    Assert(config.PositionPreset == "AboveHighway", "Position default should be AboveHighway.");

    config.SetBackgroundOpacity(expanded: false, -10.0);
    config.SetBackgroundOpacity(expanded: true, 125.0);
    config.Normalize();
    Assert(Math.Abs(config.GetBackgroundOpacity(expanded: false) - 0.0) < 0.001, "Collapsed background opacity should clamp to 0%.");
    Assert(Math.Abs(config.GetBackgroundOpacity(expanded: true) - 100.0) < 0.001, "Expanded background opacity should clamp to 100%.");

    config.VisiblePlayerCount = 8;
    config.Normalize();
    Assert(config.VisiblePlayerCount == OverlayConfig.MaxVisiblePlayerCount, "Visible players should clamp to the configured maximum.");

    config.DynamicPpScore = true;
    config.CollapsedShowPp = false;
    config.ExpandedShowPp = false;
    config.Normalize();
    Assert(!config.DynamicPpScore, "Dynamic pp/score should disable when PP is hidden in every available mode.");

    config.AlwaysExpand = false;
    config.CollapsedShowPp = true;
    config.ExpandedShowPp = true;
    Assert(config.CountAvailableShowPpOptions() == 2, "Both Show PP toggles should count when collapsed and expanded are available.");

    config.AlwaysExpand = true;
    Assert(config.CountAvailableShowPpOptions() == 1, "Only expanded Show PP should count when Always Expand is on.");

    config.ExpandedShowPp = false;
    config.DynamicPpScore = true;
    config.Normalize();
    Assert(!config.DynamicPpScore, "Dynamic pp/score should disable when Always Expand hides the only enabled collapsed Show PP option.");
}

static void RunBeatLeaderModifierPolicyTest()
{
    var preFailModifiers = BeatLeaderModifierPolicy.GetScoringModifiers(
        new[] { "Faster Song", "Slower Song", "Super Fast Song", "No Bombs", "No Obstacles", "No Notes", "No Fail" },
        includeNoFailPenalty: false);
    Assert(
        preFailModifiers.SequenceEqual(new[] { "FS", "SS", "SF", "NB", "NO", "NA" }),
        "Score-affecting modifiers were not normalized without pre-fail NF.");

    var failedModifiers = BeatLeaderModifierPolicy.GetScoringModifiers(new[] { "NF", "NA" }, includeNoFailPenalty: true);
    Assert(failedModifiers.SequenceEqual(new[] { "NF", "NA" }), "No Fail should be included only after real fail.");
    var displayPreFail = BeatLeaderModifierPolicy.GetDisplayModifiers(new[] { "BE", "No Fail", "Faster Song" }, includeNoFail: false);
    Assert(displayPreFail.SequenceEqual(new[] { "BE", "FS" }), "BeatLeader display modifiers should hide NF before real fail.");
    var displayFailed = BeatLeaderModifierPolicy.GetDisplayModifiers(new[] { "BE", "No Fail", "Faster Song" }, includeNoFail: true);
    Assert(displayFailed.SequenceEqual(new[] { "BE", "NF", "FS" }), "BeatLeader display modifiers should include NF after real fail.");
    Assert(BeatLeaderModifierPolicy.ShouldApplyNoFailPenalty(new[] { "No Fail" }, levelFailed: true), "No Fail penalty should apply after fail.");
    Assert(!BeatLeaderModifierPolicy.ShouldApplyNoFailPenalty(new[] { "No Fail" }, levelFailed: false), "No Fail penalty should not apply before fail.");
    Assert(BeatLeaderModifierPolicy.IsZenMode(new[] { "Zen Mode" }), "Zen Mode should suppress the overlay session.");
}

static void RunScoreSaberModifierPolicyTest()
{
    var preFailFound = ScoreSaberModifierPolicy.TryResolveScoreModifierMultipliers(
        new[] { "No Fail", "Slower Song", "No Bombs", "No Obstacles", "No Arrows" },
        includeNoFailPenalty: false,
        allowPositiveModifiers: false,
        out var preFailPositive,
        out var preFailNegative);
    Assert(preFailFound, "Expected ScoreSaber negative modifiers to resolve before No Fail penalty.");
    Assert(Math.Abs(preFailPositive - 1d) < 0.0001d, $"Expected no positive ScoreSaber multiplier, got {preFailPositive}.");
    Assert(Math.Abs(preFailNegative - 0.25d) < 0.0001d, $"Expected ScoreSaber negative multiplier 0.25 without NF, got {preFailNegative}.");

    var noFailFound = ScoreSaberModifierPolicy.TryResolveScoreModifierMultipliers(
        new[] { "No Fail" },
        includeNoFailPenalty: true,
        allowPositiveModifiers: false,
        out _,
        out var noFailNegative);
    Assert(noFailFound, "Expected ScoreSaber No Fail penalty to resolve after real fail.");
    Assert(Math.Abs(noFailNegative - 0.5d) < 0.0001d, $"Expected ScoreSaber NF multiplier 0.5, got {noFailNegative}.");

    var positiveDisabledFound = ScoreSaberModifierPolicy.TryResolveScoreModifierMultipliers(
        new[] { "Faster Song", "Ghost Notes" },
        includeNoFailPenalty: false,
        allowPositiveModifiers: false,
        out var positiveDisabled,
        out _);
    Assert(!positiveDisabledFound, "ScoreSaber positives should not resolve when the realm disables positive modifiers.");
    Assert(Math.Abs(positiveDisabled - 1d) < 0.0001d, $"Expected disabled ScoreSaber positive multiplier 1.0, got {positiveDisabled}.");

    var positiveEnabledFound = ScoreSaberModifierPolicy.TryResolveScoreModifierMultipliers(
        new[] { "Faster Song", "Ghost Notes" },
        includeNoFailPenalty: false,
        allowPositiveModifiers: true,
        out var positiveEnabled,
        out _);
    Assert(positiveEnabledFound, "ScoreSaber positives should resolve when the realm enables positive modifiers.");
    Assert(Math.Abs(positiveEnabled - 1.19d) < 0.0001d, $"Expected enabled ScoreSaber positive multiplier 1.19, got {positiveEnabled}.");
}

static void RunScoreSaberPpCurveTest()
{
    var pp = ScoreSaberPpCalculator.Calculate(stars: 10d, accuracy: 0.95d);
    Assert(Math.Abs(pp - 421.17208413d) < 0.0001d, $"Expected 10 star 95% ScoreSaber PP to use the curve baseline, got {pp}.");
}

static void RunScoreSaberRankingModeTest()
{
    var cache = new LeaderboardSessionCache("abc", "ExpertPlus", "Standard");
    cache.ApplyPage(new LeaderboardScoresResponse
    {
        Metadata = new LeaderboardMetadata { Page = 1, ItemsPerPage = 2, Total = 2 },
        Container = new LeaderboardContainer
        {
            LeaderboardId = "123",
            Ranked = true,
            SourceName = "ScoreSaber",
            RankByPp = false,
            SupportsPp = true,
            UsesScoreSaberPpCurve = true,
            Stars = 10d,
            MaxScore = 1000000
        },
        Data = new List<BeatLeaderScoreDto>
        {
            new() { Id = 1, Rank = 1, ModifiedScore = 900000, Accuracy = 0.94d, Pp = 100d, Player = new BeatLeaderPlayerDto { Id = "p1", Name = "A" } },
            new() { Id = 2, Rank = 2, ModifiedScore = 800000, Accuracy = 0.93d, Pp = 1000d, Player = new BeatLeaderPlayerDto { Id = "p2", Name = "B" } }
        }
    });

    var projection = new ProjectionEngine().Project(
        new BeatmapRunState
        {
            CurrentModifiedScore = 850000,
            Accuracy = 0.95d,
            KnownMaxModifiedScore = 1000000
        },
        cache,
        useRawPpRank: true);

    Assert(projection.ProjectedRank == 1, $"ScoreSaber projection must rank by accuracy, got rank {projection.ProjectedRank}.");
    Assert(projection.RankingPp == null, "ScoreSaber projection must not use PP as the ranking comparator.");
    Assert(Math.Abs(projection.ProjectedPp.GetValueOrDefault() - 421.17208413d) < 0.0001d, $"Expected ScoreSaber curve PP, got {projection.ProjectedPp}.");
}

static void RunScoreSaberFailedNoFailUsesEffectiveAccuracyTest()
{
    var cache = new LeaderboardSessionCache("abc", "ExpertPlus", "Standard");
    cache.ApplyPage(new LeaderboardScoresResponse
    {
        Metadata = new LeaderboardMetadata { Page = 1, ItemsPerPage = 3, Total = 3 },
        Container = new LeaderboardContainer
        {
            LeaderboardId = "123",
            Ranked = true,
            SourceName = "ScoreSaber",
            RankByPp = false,
            SupportsPp = true,
            UsesScoreSaberPpCurve = true,
            Stars = 10d,
            MaxScore = 1000000
        },
        Data = new List<BeatLeaderScoreDto>
        {
            new() { Id = 1, Rank = 1, ModifiedScore = 940000, Accuracy = 0.94d, Pp = 100d, Player = new BeatLeaderPlayerDto { Id = "p1", Name = "A" } },
            new() { Id = 2, Rank = 2, ModifiedScore = 480000, Accuracy = 0.48d, Pp = 10d, Player = new BeatLeaderPlayerDto { Id = "p2", Name = "B" } },
            new() { Id = 3, Rank = 3, ModifiedScore = 470000, Accuracy = 0.47d, Pp = 9d, Player = new BeatLeaderPlayerDto { Id = "p3", Name = "C" } }
        }
    });
    cache.ApplyScoreModifierMultipliers(1d, 0.5d);

    var projection = new ProjectionEngine().Project(
        new BeatmapRunState
        {
            CurrentScore = 900000,
            CurrentModifiedScore = 450000,
            Accuracy = 0.95d,
            KnownMaxModifiedScore = 1000000,
            ActiveModifiers = new[] { "No Fail" },
            IsFailedWithNoFail = true
        },
        cache,
        useRawPpRank: true);

    var expectedPp = ScoreSaberPpCalculator.Calculate(stars: 10d, accuracy: 0.475d);
    Assert(projection.ProjectedFinalScore == 475000, $"Expected failed ScoreSaber NF score to be 50% of projected score, got {projection.ProjectedFinalScore}.");
    Assert(projection.ProjectedRank == 3, $"Expected failed ScoreSaber NF rank to use effective 47.5% accuracy, got rank {projection.ProjectedRank}.");
    Assert(Math.Abs(projection.ProjectedPp.GetValueOrDefault() - expectedPp) < 0.0001d, $"Expected failed ScoreSaber NF PP to use effective 47.5% accuracy, got {projection.ProjectedPp}.");
}

static void RunScoreSaberAccuracyDoesNotFetchOutsideVisibleWindowTest()
{
    var stateMachine = new OverlayStateMachine(new OverlayConfig
    {
        VisiblePlayerCount = 4
    });
    stateMachine.StartSession(BuildScoreSaberAccuracyCacheWithLaterPlayerScore(), "local");

    var update = stateMachine.Update(new BeatmapRunState
    {
        CurrentScore = 120000,
        CurrentModifiedScore = 120000,
        Accuracy = 0.995,
        SongProgressRatio = 0.2,
        ScoredNotes = 40,
        HasRecentNotes = true
    });

    Assert(update.ViewModel.Rows.Any(row => row.PlayerName == "Top Player"), "ScoreSaber should keep already-loaded top rows visible.");
    Assert(update.PageToFetch == null, $"ScoreSaber should not hydrate pages beyond the visible rank window while locked at #1; got {update.PageToFetch}.");
}

static async Task RunScoreSaberPlayerBestSeedsLocalPageTestAsync()
{
    var apiClient = new FakeBeatLeaderApiClient { SourceNameValue = "ScoreSaber" };
    apiClient.Pages[1] = BuildPageResponse(1, 10, 350, 1, 10, true, sourceName: "ScoreSaber");
    apiClient.Pages[31] = BuildPageResponse(31, 10, 350, 301, 310, true, sourceName: "ScoreSaber");
    apiClient.PlayerBest = new BeatLeaderScoreDto
    {
        Id = 301,
        Rank = 301,
        ModifiedScore = 907867,
        Accuracy = 0.9433d,
        Pp = 341.6d,
        Player = new BeatLeaderPlayerDto { Id = "local", Name = "Local" }
    };

    var manager = new BeatLeaderOverlaySessionManager(
        apiClient,
        new OverlayStateMachine(new OverlayConfig { VisiblePlayerCount = 2 }),
        new NullOverlayLogger());

    var start = await manager.StartAsync(
        new BeatmapSessionInfo { Hash = "ABCDEF", Difficulty = "Expert", Mode = "Standard" },
        "local",
        CancellationToken.None);

    Assert(start.IsStarted, "Expected ScoreSaber session to start.");
    Assert(start.Cache?.ScoresByRank.ContainsKey(301) == true, "Expected ScoreSaber player best row to seed the session cache.");
    Assert(start.Cache?.FetchedPages.Contains(31) == true, "Expected ScoreSaber player best page to be fetched once at session start.");
}

static void RunLeaderboardContainerModifierValuesTest()
{
    using var document = JsonDocument.Parse("""
        {
          "leaderboard": {
            "difficulty": {
              "modifierValues": {
                "fs": 0.2,
                "nb": -0.2
              }
            }
          }
        }
        """);

    var container = new LeaderboardContainer
    {
        ExtraData = new Dictionary<string, JsonElement>
        {
            ["leaderboard"] = document.RootElement.GetProperty("leaderboard").Clone()
        }
    };

    var found = container.TryResolveScoreModifierMultipliers(new[] { "Faster Song", "No Bombs" }, out var positive, out var negative);
    Assert(found, "Expected leaderboard container modifierValues to be resolved.");
    Assert(Math.Abs(positive - 1.2d) < 0.0001d, $"Expected FS positive multiplier 1.2, got {positive}.");
    Assert(Math.Abs(negative - 0.8d) < 0.0001d, $"Expected NB negative multiplier 0.8, got {negative}.");
}

static void RunLeaderboardCacheAndProjectionTest()
{
    var cache = BuildCache();
    var runState = new BeatmapRunState
    {
        CurrentModifiedScore = 450000,
        SongProgressRatio = 0.5,
        ScoredNotes = 160
    };

    var projection = new ProjectionEngine().Project(runState, cache);

    Assert(projection.HasProjection, "Projection was not produced.");
    Assert(projection.ProjectedFinalScore >= runState.CurrentModifiedScore, "Projected score should never drop below current score.");
    Assert(projection.ProjectedFinalScore <= 900000, "Projection should stay bounded by available score data.");
    Assert(projection.ProjectedRank is >= 1 and <= 4, $"Unexpected projected rank: {projection.ProjectedRank}.");
    Assert(!projection.ExactRankCovered, "Expected sparse rank coverage near projected insertion point.");
    Assert(projection.NearbyRows.Count >= 1, "Expected at least one nearby row around projected rank.");
}

static void RunUnrankedModifierProjectionTest()
{
    var cache = BuildUnrankedModifierCache();
    var runState = new BeatmapRunState
    {
        CurrentModifiedScore = 900000,
        Accuracy = 0.9,
        SongProgressRatio = 0.5,
        ScoredNotes = 160,
        ActiveModifiers = new[] { "No Bombs" }
    };

    var projection = new ProjectionEngine().Project(runState, cache);

    Assert(projection.ProjectedFinalScore == 720000, $"Expected unranked projected score to apply negative modifier formula, got {projection.ProjectedFinalScore}.");
    Assert(projection.ProjectedRank == 2, $"Expected negative-modified unranked projection to rank second, got {projection.ProjectedRank}.");
}

static void RunUnrankedPositiveModifierProjectionTest()
{
    var cache = BuildUnrankedPositiveModifierCache();
    var runState = new BeatmapRunState
    {
        CurrentModifiedScore = 650897,
        Accuracy = 0.9556,
        SongProgressRatio = 1.0,
        ScoredNotes = 765,
        ActiveModifiers = new[] { "Faster Song" }
    };

    var projection = new ProjectionEngine().Project(runState, cache);

    Assert(cache.PositiveScoreModifierMultiplier == 1.2d, "Fixture should exercise the positive gap-bonus formula.");
    Assert(projection.ProjectedFinalScore is >= 656940 and <= 656950, $"Expected BeatLeader positive modifier formula score, got {projection.ProjectedFinalScore}.");
    Assert(projection.ProjectedRank == 2, $"Expected positive-modified unranked projection to rank second, got {projection.ProjectedRank}.");
}

static void RunUnrankedInfersMaxScoreFromRowsTest()
{
    var response = new LeaderboardScoresResponse
    {
        Metadata = new LeaderboardMetadata
        {
            Page = 1,
            ItemsPerPage = 5,
            Total = 5
        },
        Container = new LeaderboardContainer
        {
            LeaderboardId = "leaderboard-v5-no-max-score",
            Ranked = false
        },
        Data =
        {
            new BeatLeaderScoreDto { Id = 1, Rank = 1, BaseScore = 667992, ModifiedScore = 667992, Accuracy = 0.98067546, Player = new BeatLeaderPlayerDto { Id = "p1", Name = "VortexWizrd" } },
            new BeatLeaderScoreDto { Id = 2, Rank = 2, BaseScore = 650897, ModifiedScore = 656948, Accuracy = 0.9555784, Modifiers = "FS", Player = new BeatLeaderPlayerDto { Id = "p2", Name = "BSMben" } },
            new BeatLeaderScoreDto { Id = 3, Rank = 3, BaseScore = 656519, ModifiedScore = 656519, Accuracy = 0.963832, Player = new BeatLeaderPlayerDto { Id = "p3", Name = "darkrelicer" } },
            new BeatLeaderScoreDto { Id = 4, Rank = 4, BaseScore = 654908, ModifiedScore = 654908, Accuracy = 0.9614669, Player = new BeatLeaderPlayerDto { Id = "p4", Name = "LSylli" } },
            new BeatLeaderScoreDto { Id = 5, Rank = 5, BaseScore = 650784, ModifiedScore = 650784, Accuracy = 0.9554125, Player = new BeatLeaderPlayerDto { Id = "p5", Name = "KINAKONEJIRI" } }
        }
    };

    var cache = new LeaderboardSessionCache("37461e3f7875cb363194f50f21da669d37d47954", "Expert", "Standard");
    cache.ApplyPage(response);
    cache.ApplyScoreModifierMultipliers(1.2d, 1d);

    var projection = new ProjectionEngine().Project(
        new BeatmapRunState
        {
            CurrentModifiedScore = 650897,
            Accuracy = 0.9555784,
            SongProgressRatio = 1,
            ScoredNotes = 765,
            ActiveModifiers = new[] { "Faster Song" }
        },
        cache);

    Assert(cache.MaxScore is >= 681140 and <= 681170, $"Expected inferred max score around 681155, got {cache.MaxScore}.");
    Assert(projection.ProjectedFinalScore == 656948, $"Expected inferred-max FS score 656948, got {projection.ProjectedFinalScore}.");
    Assert(projection.ProjectedRank == 2, $"Expected inferred-max FS projection to rank second, got {projection.ProjectedRank}.");
}

static void RunUnrankedPositiveModifierUsesClassicFinalScoreTest()
{
    var cache = BuildUnrankedPositiveModifierCache();
    var projection = new ProjectionEngine().Project(
        new BeatmapRunState
        {
            CurrentModifiedScore = 10000,
            Accuracy = 0.9556,
            SongProgressRatio = 0.05,
            ScoredNotes = 30,
            ActiveModifiers = new[] { "Faster Song" }
        },
        cache);

    Assert(projection.ProjectedFinalScore is >= 656940 and <= 656970, $"Expected BeatLeader unranked FS to rank from classic final score, got {projection.ProjectedFinalScore}.");
}

static void RunBeatLeaderUnrankedDisplaysCurrentScoreWhileRankingClassicFinalScore()
{
    var stateMachine = new OverlayStateMachine(new OverlayConfig { VisiblePlayerCount = 3 });
    stateMachine.StartSession(BuildUnrankedPositiveModifierCache(), "local");

    var update = stateMachine.Update(new BeatmapRunState
    {
        CurrentModifiedScore = 10000,
        Accuracy = 0.9556,
        SongProgressRatio = 0.05,
        ScoredNotes = 30,
        ActiveModifiers = new[] { "Faster Song" }
    });

    var localRow = update.ViewModel.Rows.FirstOrDefault(row => row.IsLocalPlayer);
    Assert(update.ViewModel.RankText.StartsWith("#", StringComparison.Ordinal), $"Expected classic final-score rank text, got {update.ViewModel.RankText}.");
    Assert(localRow?.ValueText == "10,000", $"Expected BeatLeader unranked overlay to display current score, got {localRow?.ValueText}.");
    Assert(update.ViewModel.ProjectedScore == 10000, $"Expected displayed projected score to remain current score, got {update.ViewModel.ProjectedScore}.");
}

static void RunUnrankedLeaderboardHidesPpEvenWhenApiClaimsSupport()
{
    var cache = BuildUnrankedPositiveModifierCache(apiSupportsPp: true);
    var stateMachine = new OverlayStateMachine(new OverlayConfig
    {
        DynamicPpScore = true,
        ExpandedShowPp = true,
        ExpandedShowScore = false,
        PreviewExpanded = true,
        VisiblePlayerCount = 3
    });
    stateMachine.StartSession(cache, "local");

    var update = stateMachine.Update(new BeatmapRunState
    {
        CurrentModifiedScore = 10000,
        Accuracy = 0.9556,
        SongProgressRatio = 0.05,
        ScoredNotes = 30,
        ActiveModifiers = new[] { "Faster Song" },
        IsPaused = true
    });

    var localRow = update.ViewModel.Rows.FirstOrDefault(row => row.IsLocalPlayer);
    Assert(update.ViewModel.SupportsPp, "Unranked BeatLeader should keep API PP capability so non-PP row details stay independent.");
    Assert(update.ViewModel.Rows.All(row => !row.ValueText.EndsWith("pp", StringComparison.OrdinalIgnoreCase)), "Unranked rows should not display PP text.");
    Assert(localRow?.ValueText == "10,000", $"Unranked local row should display current score instead of PP text, got {localRow?.ValueText}.");
    Assert(update.ViewModel.Rows.Any(row => row.IsLocalPlayer && row.Score == 10000), "Dynamic pp/score should still make current score available on unranked maps.");
}

static void RunBeatLeaderUnrankedClassicScoreWithoutScoreModifiersTest()
{
    var cache = BuildUnrankedPositiveModifierCache();
    var projection = new ProjectionEngine().Project(
        new BeatmapRunState
        {
            CurrentModifiedScore = 123456,
            Accuracy = 0.9556,
            SongProgressRatio = 0.5,
            ScoredNotes = 300
        },
        cache);

    Assert(projection.ProjectedFinalScore is >= 650890 and <= 650920, $"Expected unranked BeatLeader no-mod projection to use classic final score, got {projection.ProjectedFinalScore}.");
}

static void RunBeatLeaderUnrankedClassicScoreWithNonScoringModifiersTest()
{
    var cache = BuildUnrankedPositiveModifierCache();
    var projection = new ProjectionEngine().Project(
        new BeatmapRunState
        {
            CurrentModifiedScore = 234567,
            Accuracy = 0.9556,
            SongProgressRatio = 0.5,
            ScoredNotes = 300,
            ActiveModifiers = new[] { "Zen Mode" }
        },
        cache);

    Assert(projection.ProjectedFinalScore is >= 650890 and <= 650920, $"Expected non-scoring modifier projection to use classic final score, got {projection.ProjectedFinalScore}.");
}

static void RunBeatLeaderUnrankedClassicScoreWithNoFailBeforeFailTest()
{
    var cache = BuildUnrankedModifierCache();
    var projection = new ProjectionEngine().Project(
        new BeatmapRunState
        {
            CurrentModifiedScore = 345678,
            Accuracy = 0.9,
            SongProgressRatio = 0.5,
            ScoredNotes = 300,
            ActiveModifiers = new[] { "No Fail" },
            IsFailedWithNoFail = false
        },
        cache);

    Assert(projection.ProjectedFinalScore == 900000, $"Expected pre-fail NF projection to use classic final score without NF penalty, got {projection.ProjectedFinalScore}.");
}

static void RunProjectionAppliesNoFailAfterFailTest()
{
    var cache = BuildUnrankedModifierCache();
    var projection = new ProjectionEngine().Project(
        new BeatmapRunState
        {
            CurrentModifiedScore = 900000,
            Accuracy = 0.9,
            SongProgressRatio = 0.5,
            ScoredNotes = 300,
            ActiveModifiers = new[] { "No Fail" },
            IsFailedWithNoFail = true
        },
        cache);

    Assert(projection.ProjectedFinalScore == 720000, $"Expected failed NF projection to apply score modifier formula, got {projection.ProjectedFinalScore}.");
}

static void RunConfidenceGateTest()
{
    var cache = BuildCache();
    var projectionEngine = new ProjectionEngine();
    var confidence = new ConfidenceEngine();

    var firstNoteRun = new BeatmapRunState
    {
        CurrentModifiedScore = 10000,
        SongProgressRatio = 0.01,
        ScoredNotes = 1
    };

    var earlyProjection = projectionEngine.Project(firstNoteRun, cache);
    var earlyDecision = confidence.Evaluate(firstNoteRun, earlyProjection);
    Assert(!earlyDecision.CanShowNumericRank, "Numeric rank should be hidden after the first note.");
    Assert(earlyDecision.DisplayText.StartsWith("#", StringComparison.Ordinal), "Confidence gate should still show a numeric placeholder rank.");

    var credibleRun = new BeatmapRunState
    {
        CurrentModifiedScore = 450000,
        SongProgressRatio = 0.5,
        ScoredNotes = 160
    };

    var projection = projectionEngine.Project(credibleRun, cache);
    var firstDecision = confidence.Evaluate(credibleRun, projection);
    var secondDecision = confidence.Evaluate(credibleRun, projection);

    Assert(firstDecision.DisplayText.StartsWith("#", StringComparison.Ordinal), "First decision should still be a numeric rank placeholder.");
    Assert(secondDecision.CanShowNumericRank, "Numeric rank should show after confidence gates and stable projection.");
    Assert(secondDecision.DisplayText.StartsWith("#", StringComparison.Ordinal), $"Unexpected confidence display text: {secondDecision.DisplayText}.");
}

static void RunRankSmootherTest()
{
    var smoother = new RankSmoother();

    var initial = smoother.Update(20, 0.3);
    var smallAccepted = smoother.Update(19, 0.3);
    var largeMove = smoother.Update(14, 0.3);

    Assert(initial.Rank == 20, "Initial smoothed rank should use raw rank.");
    Assert(smallAccepted.Rank == 19, "Small moves should update once the early-song gate has passed.");
    Assert(largeMove.Rank == 14, "Larger move should update immediately.");

    smoother.Reset();
    var earlyInitial = smoother.Update(20, 0.03);
    var earlyPending = smoother.Update(19, 0.03);
    var earlyConfirmed = smoother.Update(19, 0.03);
    Assert(earlyInitial.Rank == 20, "Initial early rank should use raw rank.");
    Assert(earlyPending.Rank == 20, "Small early-song flicker should be suppressed for one cycle.");
    Assert(earlyConfirmed.Rank == 19, "Stable early-song small move should eventually be accepted.");
}

static void RunOverlayStateMachineTest()
{
    var config = new OverlayConfig();
    var stateMachine = new OverlayStateMachine(config);
    stateMachine.StartSession(BuildCache(), "local-player-id");

    var runState = new BeatmapRunState
    {
        CurrentModifiedScore = 450000,
        SongProgressRatio = 0.5,
        ScoredNotes = 160,
        HasRecentNotes = true,
        HasUpcomingScorableNote = true,
        SecondsUntilNextScorableNote = 1.0
    };

    var first = stateMachine.Update(runState);
    var second = stateMachine.Update(runState);

    Assert(first.ViewModel.Mode == OverlayMode.Collapsed, $"Expected collapsed overlay, got {first.ViewModel.Mode}.");
    Assert(second.ViewModel.Mode == OverlayMode.Collapsed, $"Expected collapsed overlay, got {second.ViewModel.Mode}.");
    Assert(second.ViewModel.DisplayName == "You", "Player label must always be You.");
    Assert(second.ViewModel.RankText.StartsWith("#", StringComparison.Ordinal), $"Unexpected rank text: {second.ViewModel.RankText}.");
    Assert(second.ViewModel.Rows.Any(row => row.IsLocalPlayer && row.IsProjected), "Projected local row was not included.");
    var projectedRank = second.ViewModel.Rows.First(row => row.IsLocalPlayer).Rank;
    Assert(second.ViewModel.Rows.Count(row => row.Rank == projectedRank) == 1, "Projected row should not duplicate an existing row at the same displayed rank.");

    runState.IsPaused = true;
    var paused = stateMachine.Update(runState);
    Assert(paused.ViewModel.Mode == OverlayMode.Expanded, "Pause should switch to expanded mode.");

    runState.IsPaused = false;
    runState.IsReplayMode = true;
    var replay = stateMachine.Update(runState);
    Assert(replay.ViewModel.Mode == OverlayMode.Expanded, "Replay should keep the overlay expanded.");

    runState.IsReplayMode = false;
    stateMachine.SetRuntimeAlwaysExpand(true);
    var replayOverride = stateMachine.Update(runState);
    Assert(replayOverride.ViewModel.Mode == OverlayMode.Expanded, "Runtime replay override should mimic AlwaysExpand.");

    stateMachine.SetRuntimeAlwaysExpand(false);
    var afterReplay = stateMachine.Update(runState);
    Assert(afterReplay.ViewModel.Mode == OverlayMode.Collapsed, "Runtime replay override should clear after replay exit.");
}

static void RunReplayAlwaysExpandOverrideTest()
{
    var config = new OverlayConfig { AlwaysExpand = false };
    var replayOverride = new ReplayAlwaysExpandOverride(config);

    replayOverride.Enable();
    Assert(config.AlwaysExpand, "Replay override should use the same AlwaysExpand setting path.");

    replayOverride.Restore();
    Assert(!config.AlwaysExpand, "Replay override should restore a disabled user AlwaysExpand setting.");

    config.AlwaysExpand = true;
    replayOverride.Enable();
    replayOverride.Restore();
    Assert(config.AlwaysExpand, "Replay override should preserve an enabled user AlwaysExpand setting.");
}

static void RunOverlayBreakExpansionRulesTest()
{
    var config = new OverlayConfig
    {
        ExpandOnBreakSeconds = 3.0
    };

    var stateMachine = new OverlayStateMachine(config);
    stateMachine.StartSession(BuildCache(), "local-player-id");

    var breakGap = stateMachine.Update(new BeatmapRunState
    {
        CurrentModifiedScore = 500000,
        SongProgressRatio = 0.3,
        ScoredNotes = 100,
        SongTimeSeconds = 10.0,
        HasRecentNotes = true,
        IsInPlannedBreak = true,
        BreakWindowStartTimeSeconds = 10.0,
        BreakWindowEndTimeSeconds = 15.0
    });
    Assert(breakGap.ViewModel.Mode == OverlayMode.Expanded, "Timeline break window should expand.");

    var inactivity = stateMachine.Update(new BeatmapRunState
    {
        CurrentModifiedScore = 500000,
        SongProgressRatio = 0.4,
        ScoredNotes = 120,
        SongTimeSeconds = 20.0,
        HasRecentNotes = false,
        SecondsSinceLastNote = 3.1,
        HasUpcomingScorableNote = true,
        SecondsUntilNextScorableNote = 1.5
    });
    Assert(inactivity.ViewModel.Mode == OverlayMode.Collapsed, "Inactivity without a timeline break must not expand.");

    var outro = stateMachine.Update(new BeatmapRunState
    {
        CurrentModifiedScore = 700000,
        SongProgressRatio = 0.95,
        ScoredNotes = 300,
        SongTimeSeconds = 30.0,
        IsAfterLastNote = true
    });
    Assert(outro.ViewModel.Mode == OverlayMode.Expanded, "After last note should expand immediately.");

    var denseGameplay = stateMachine.Update(new BeatmapRunState
    {
        CurrentModifiedScore = 520000,
        SongProgressRatio = 0.5,
        ScoredNotes = 140,
        SongTimeSeconds = 31.0,
        HasRecentNotes = true,
        HasUpcomingScorableNote = true,
        SecondsUntilNextScorableNote = 0.4
    });
    Assert(denseGameplay.ViewModel.Mode == OverlayMode.Collapsed, "Dense gameplay should stay collapsed.");

    var paused = stateMachine.Update(new BeatmapRunState
    {
        CurrentModifiedScore = 520000,
        SongProgressRatio = 0.5,
        ScoredNotes = 140,
        IsPaused = true
    });
    Assert(paused.ViewModel.Mode == OverlayMode.Expanded, "Paused should expand.");
}

static void RunOverlayTimelineOnlyBreakBehaviorTest()
{
    var config = new OverlayConfig
    {
        ExpandOnBreakSeconds = 5.0
    };

    var stateMachine = new OverlayStateMachine(config);
    stateMachine.StartSession(BuildCache(), "local-player-id");

    var silence = stateMachine.Update(new BeatmapRunState
    {
        CurrentModifiedScore = 400000,
        SongProgressRatio = 0.25,
        ScoredNotes = 80,
        HasRecentNotes = false,
        SecondsSinceLastNote = 5.1,
        HasUpcomingScorableNote = true,
        SecondsUntilNextScorableNote = 2.0
    });
    Assert(silence.ViewModel.Mode == OverlayMode.Collapsed, "Silence must not expand without a timeline break.");

    var upcomingGapOnly = stateMachine.Update(new BeatmapRunState
    {
        CurrentModifiedScore = 400000,
        SongProgressRatio = 0.26,
        ScoredNotes = 82,
        SongTimeSeconds = 12.0,
        HasRecentNotes = true,
        HasUpcomingScorableNote = true,
        SecondsUntilNextScorableNote = 6.0
    });
    Assert(upcomingGapOnly.ViewModel.Mode == OverlayMode.Collapsed, "Upcoming-note gap hints must not expand without a precomputed break window.");

    var oldRuntimeTrigger = stateMachine.Update(new BeatmapRunState
    {
        CurrentModifiedScore = 420000,
        SongProgressRatio = 0.30,
        ScoredNotes = 90,
        HasReliableRuntimeBreakDetector = true,
        IsRuntimeBreakExpansionActive = true,
        HasUpcomingScorableNote = true,
        SecondsUntilNextScorableNote = 1.0
    });
    Assert(oldRuntimeTrigger.ViewModel.Mode == OverlayMode.Collapsed, "Note-hit runtime break flags must not expand the overlay.");

    var timelineBreak = stateMachine.Update(new BeatmapRunState
    {
        CurrentModifiedScore = 500000,
        SongProgressRatio = 0.35,
        ScoredNotes = 100,
        SongTimeSeconds = 30.0,
        HasReliableRuntimeBreakDetector = true,
        IsInPlannedBreak = true,
        BreakWindowStartTimeSeconds = 30.0,
        BreakWindowEndTimeSeconds = 36.0
    });
    Assert(timelineBreak.ViewModel.Mode == OverlayMode.Expanded, "Precomputed timeline break should expand regardless of note-hit state.");
}

static void RunOverlayRememberedOpponentRowsTest()
{
    var stateMachine = new OverlayStateMachine(new OverlayConfig { VisiblePlayerCount = 3 });
    var cache = BuildCache();
    stateMachine.StartSession(cache, "local-player-id");

    var first = stateMachine.Update(new BeatmapRunState
    {
        CurrentModifiedScore = 850000,
        SongProgressRatio = 0.5,
        ScoredNotes = 160,
        HasRecentNotes = true
    });

    Assert(first.ViewModel.Rows.Any(row => row.PlayerName == "Player B"), "Expected Player B to be visible before rank movement.");
    var field = typeof(LeaderboardSessionCache).GetField("scoresByRank", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
    var byRank = field?.GetValue(cache) as Dictionary<int, BeatLeaderScoreRow>;
    byRank?.Remove(2);

    var later = stateMachine.Update(new BeatmapRunState
    {
        CurrentModifiedScore = 850000,
        SongProgressRatio = 0.5,
        ScoredNotes = 160,
        HasRecentNotes = true
    });

    Assert(later.ViewModel.Rows.Any(row => row.Rank == 2 && row.PlayerName == "Player B"), "Previously resolved rank 2 should not fall back to Loading.");
}

static void RunOverlayRangeFetchHintTest()
{
    var stateMachine = new OverlayStateMachine(new OverlayConfig { VisiblePlayerCount = 4 });
    stateMachine.StartSession(BuildSparseCache(), null);

    var runState = new BeatmapRunState
    {
        CurrentModifiedScore = 350000,
        SongProgressRatio = 0.5,
        ScoredNotes = 160,
        HasRecentNotes = true,
        HasUpcomingScorableNote = true,
        SecondsUntilNextScorableNote = 1.0
    };

    _ = stateMachine.Update(runState);
    var second = stateMachine.Update(runState);

    Assert(second.ViewModel.Mode == OverlayMode.Collapsed, "Broad range should still produce a collapsed overlay after confidence gates.");
    Assert(second.ViewModel.RankText.StartsWith("#", StringComparison.Ordinal), $"Expected numeric rank text, got {second.ViewModel.RankText}.");
    Assert(second.PageToFetch.HasValue && second.PageToFetch.Value >= 2, $"Expected a fetch hint beyond page 1, got {second.PageToFetch}.");
    Assert(second.ViewModel.Rows.Any(row => row.PlayerName == "Loading..."), "Expected placeholder rows for unfetched ranks.");
}

static void RunOverlayExpansionTransitionTest()
{
    foreach (var fps in new[] { 30, 60, 90, 120, 144 })
    {
        var animation = new OverlayExpansionTransition();
        var frames = (int)Math.Ceiling(OverlayExpansionTransition.DurationSeconds * fps);
        for (var frame = 0; frame < frames; frame++) animation.Advance(1d, 1d / fps);
        Assert(animation.Value == 1d, $"Expansion must finish at {fps}fps.");
        for (var frame = 0; frame < frames; frame++) animation.Advance(0d, 1d / fps);
        Assert(animation.Value == 0d, $"Collapse must finish at {fps}fps.");
    }

    var opening = new OverlayExpansionTransition();
    var closing = new OverlayExpansionTransition();
    closing.Reset(1d);
    var halfOpen = opening.Advance(1d, 0.12d);
    var halfClosed = closing.Advance(0d, 0.12d);
    Assert(halfOpen > 0.9d && halfOpen < 1d, "Exponential motion should travel quickly and settle near the endpoint.");
    Assert(Math.Abs(halfOpen + halfClosed - 1d) < 0.000001d, "Opening and closing should have matching ease-out motion.");
    Assert(opening.Advance(0d, 0d) == halfOpen, "Reversing must preserve the current position without a jump.");
    Assert(opening.Advance(0d, 0.04d) < halfOpen, "Reversal should move toward the new target immediately.");
    opening.Advance(0d, 0.24d);
    Assert(!OverlayExpansionTransition.IsInProgress(opening.Value, 0d), "Collapse should settle exactly.");
    opening.Reset(1d);
    Assert(opening.Advance(1d, 0.01d) == 1d, "Replay's immediate expanded reset should remain stable.");

    // Exhaust the six-column visibility combinations; disabled settings must not
    // manufacture space, and equal settings must produce identical geometry.
    for (var mask = 0; mask < 64; mask++)
    {
        var widths = Enumerable.Range(0, 6).Select(column => (mask & (1 << column)) != 0 ? 0.1d * (column + 1) : 0d).ToArray();
        var layout = new OverlayContentLayout(widths, 0.02d, 0.05d);
        var same = new OverlayContentLayout(widths, 0.02d, 0.05d);
        var count = widths.Count(width => width > 0d);
        var expected = widths.Sum() + Math.Max(0, count - 1) * 0.02d + 0.1d;
        Assert(Math.Abs(layout.Width - expected) < 0.000001d, "Panel width should contain only enabled content, gaps and padding.");
        Assert(layout.Width == same.Width, "Equal mode options should not expand the panel.");
    }

    var compact = new OverlayContentLayout(new[] { 0.1d, 0.3d, 0d, 0.2d }, 0.02d, 0.05d);
    var detailed = new OverlayContentLayout(new[] { 0.1d, 0.3d, 0.15d, 0.2d }, 0.02d, 0.05d);
    Assert(detailed.Width > compact.Width, "Adding data should grow the panel; removing it should shrink it.");
    Assert(compact.Positions[0] == detailed.Positions[0] && compact.Positions[1] == detailed.Positions[1], "Adding values must not widen the rank-to-name gap.");
    Assert(OverlayContentLayout.InterpolatePosition(double.NaN, 0.4d, 0d) == 0.4d, "Incoming fields should reveal at their column position.");
    Assert(OverlayContentLayout.FieldOpacity(true, true, 0.5d) == 1d, "Shared values must stay visible.");
    Assert(OverlayContentLayout.FieldOpacity(false, true, 0d) == 0d, "Incoming values must start hidden.");
    Assert(OverlayContentLayout.FieldOpacity(true, false, 0.45d) == 0d, "Removed values should clear before closing the gap.");
    Assert(OverlayContentLayout.Reveal(0.15d) == 0d && OverlayContentLayout.Reveal(1d) == 1d, "The swipe should start after the short lead-in and uncover everything.");
}

static void RunOverlayMissingVisibleRankRefreshesFetchedPageTest()
{
    var stateMachine = new OverlayStateMachine(new OverlayConfig { VisiblePlayerCount = 2 });
    stateMachine.StartSession(BuildMissingTopRankCache(), "local-player-id");

    var runState = new BeatmapRunState
    {
        CurrentModifiedScore = 998500,
        Accuracy = 0.995,
        SongProgressRatio = 0.5,
        ScoredNotes = 160,
        HasRecentNotes = true,
        HasUpcomingScorableNote = true
    };

    _ = stateMachine.Update(runState);
    var update = stateMachine.Update(runState);

    Assert(update.ViewModel.Rows.Any(row => row.Rank == 1 && row.PlayerName == "Loading..."), "Expected rank 1 to be a visible loading placeholder.");
    Assert(update.PageToFetch == 1, $"Expected missing visible rank to refresh fetched page 1, got {update.PageToFetch}.");
    Assert(update.ForcePageRefresh, "Expected fetched page refresh to be marked as forced.");
}

static void RunOverlayRawPpModeTest()
{
    var config = new OverlayConfig
    {
        UseRawPpRank = true
    };

    var stateMachine = new OverlayStateMachine(config);
    stateMachine.StartSession(BuildRankedCacheWithPp(), "local-player-id");

    var runState = new BeatmapRunState
    {
        CurrentModifiedScore = 450000,
        SongProgressRatio = 0.01,
        ScoredNotes = 1,
        Accuracy = 0.965,
        HasRecentNotes = true
    };

    var update = stateMachine.Update(runState);
    Assert(update.ViewModel.RankText.StartsWith("#", StringComparison.Ordinal), "Raw PP mode should show immediate numeric rank.");
}

static void RunRankedAlwaysDisplaysPpTest()
{
    var config = new OverlayConfig
    {
        UseRawPpRank = false
    };

    var stateMachine = new OverlayStateMachine(config);
    stateMachine.StartSession(BuildRankedCacheWithPp(), "local-player-id");

    var runState = new BeatmapRunState
    {
        CurrentModifiedScore = 450000,
        SongProgressRatio = 0.5,
        ScoredNotes = 160,
        Accuracy = 0.965,
        HasRecentNotes = true
    };

    _ = stateMachine.Update(runState);
    var update = stateMachine.Update(runState);
    Assert(update.ViewModel.Rows.Count > 0, "Expected ranked PP rows.");
    Assert(update.ViewModel.Rows.All(row => row.ValueText == "--" || row.ValueText.EndsWith("pp", StringComparison.Ordinal)), "Ranked rows must display PP, not score.");
}

static void RunBeatLeaderPpRowsPromoteRankedDisplayTest()
{
    var cache = BuildBeatLeaderPpRowsWithFalseRankedFlag();
    var stateMachine = new OverlayStateMachine(new OverlayConfig
    {
        DynamicPpScore = true,
        ExpandedShowPp = true,
        ExpandedShowScore = false,
        PreviewExpanded = true,
        VisiblePlayerCount = 3
    });
    stateMachine.StartSession(cache, "local");

    var update = stateMachine.Update(new BeatmapRunState
    {
        CurrentModifiedScore = 900000,
        Accuracy = 0.95,
        SongProgressRatio = 0.5,
        ScoredNotes = 160,
        IsPaused = true
    });

    var localRow = update.ViewModel.Rows.FirstOrDefault(row => row.IsLocalPlayer);
    Assert(update.ViewModel.IsRanked, "BeatLeader score rows with PP should be treated as ranked even if the score page ranked flag is false.");
    Assert(update.ViewModel.SupportsPp, "BeatLeader score rows with PP should keep PP display support.");
    Assert(localRow?.ValueText?.EndsWith("pp", StringComparison.Ordinal) == true, $"Expected ranked BeatLeader local row to display PP, got {localRow?.ValueText}.");
}

static void RunRankedPpComparisonDoesNotUnderrankAfterEarlyGateTest()
{
    var cache = BuildRankedPpComparisonCache();
    var runState = new BeatmapRunState
    {
        CurrentModifiedScore = 900000,
        SongProgressRatio = 0.5,
        ScoredNotes = 160,
        Accuracy = 0.95,
        HasRecentNotes = true
    };

    var projection = new ProjectionEngine().Project(runState, cache, useRawPpRank: true);
    Assert(projection.ProjectedPp is >= 545 and <= 555, $"Expected projected PP around 550, got {projection.ProjectedPp}.");
    Assert(projection.RankingPp == projection.ProjectedPp, "After the early gate, ranked comparison should use the same PP shown to the player.");
    Assert(projection.ProjectedRank == 2, $"550pp should not rank below 250pp rows, got rank {projection.ProjectedRank}.");

    var stateMachine = new OverlayStateMachine(new OverlayConfig { UseRawPpRank = true });
    stateMachine.StartSession(cache, "local");
    _ = stateMachine.Update(runState);
    var update = stateMachine.Update(runState);
    var localRow = update.ViewModel.Rows.FirstOrDefault(row => row.IsLocalPlayer);
    Assert(localRow?.ValueText == "550.0pp", $"Expected displayed PP to match ranked comparison PP, got {localRow?.ValueText}.");
}

static void RunBeatLeaderCurveUsesFetchedRatingsTest()
{
    var cache = new LeaderboardSessionCache("c73978fbd4da71c765f0538d676a979535012d8a", "Expert", "Standard");
    cache.ApplyPage(new LeaderboardScoresResponse
    {
        Metadata = new LeaderboardMetadata
        {
            Page = 1,
            ItemsPerPage = 2,
            Total = 3065
        },
        Container = new LeaderboardContainer
        {
            LeaderboardId = "1bb8d71",
            Ranked = true,
            MaxScore = 1149195
        },
        Data =
        {
            new BeatLeaderScoreDto { Id = 1, Rank = 1, BaseScore = 1127880, ModifiedScore = 1127880, Accuracy = 0.9814522, Pp = 797.8356, Player = new BeatLeaderPlayerDto { Id = "top", Name = "Top" } },
            new BeatLeaderScoreDto { Id = 2, Rank = 2, BaseScore = 1100000, ModifiedScore = 1100000, Accuracy = 0.957, Pp = 520, Player = new BeatLeaderPlayerDto { Id = "second", Name = "Second" } }
        }
    });
    cache.ApplyDifficultyRatings(
        stars: 9.955169,
        predictedAcc: 0.975241,
        passRating: 10.316791,
        accRating: 10.143687,
        techRating: 3.6494896);

    var projection = new ProjectionEngine().Project(
        new BeatmapRunState
        {
            CurrentModifiedScore = 450000,
            Accuracy = 0.9629,
            KnownMaxModifiedScore = 1149195,
            SongProgressRatio = 0.4,
            ScoredNotes = 400
        },
        cache,
        useRawPpRank: true);

    Assert(projection.ProjectedPp is > 525 and < 550, $"Expected BeatLeader curve PP near 536, got {projection.ProjectedPp}.");
}

static void RunRankedGapProtectionTest()
{
    var config = new OverlayConfig
    {
        UseRawPpRank = true
    };

    var stateMachine = new OverlayStateMachine(config);
    stateMachine.StartSession(BuildSparseRankedCacheWithGap(), "local");
    var runState = new BeatmapRunState
    {
        CurrentModifiedScore = 930000,
        SongProgressRatio = 0.85,
        ScoredNotes = 500,
        Accuracy = 0.985,
        HasRecentNotes = true
    };

    _ = stateMachine.Update(runState);
    var update = stateMachine.Update(runState);
    var localRow = update.ViewModel.Rows.FirstOrDefault(row => row.IsLocalPlayer);
    Assert(localRow != null, "Expected local row in ranked gap test.");
    Assert(localRow!.Rank <= 4, $"Rank should stay at contiguous boundary when gaps exist, got {localRow.Rank}.");
}

static void RunKnownRankSegmentProjectionTest()
{
    var cache = BuildRankedCacheWithKnownLaterPage();
    var runState = new BeatmapRunState
    {
        CurrentModifiedScore = 900000,
        SongProgressRatio = 0.85,
        ScoredNotes = 500,
        Accuracy = 0.965,
        HasRecentNotes = true
    };

    var projection = new ProjectionEngine().Project(runState, cache, useRawPpRank: true);
    Assert(projection.ProjectedRank == 101, $"Sparse page gaps should stop at the first unknown boundary, got rank {projection.ProjectedRank}.");
    Assert(!projection.ExactRankCovered, "Boundary rank should stay inexact until the adjacent missing page is loaded.");
    Assert(projection.SuggestedPageToFetch == 2, $"Boundary projection should fetch the adjacent missing page, got {projection.SuggestedPageToFetch}.");
}

static void RunProjectionUsesBroadScannedSegmentsTest()
{
    var cache = new LeaderboardSessionCache("ABCDEF", "ExpertPlus", "Standard");
    cache.ApplyPage(BuildPageResponse(1, 10, 1_000, 1, 10, true));
    cache.ApplyScannedPage(BuildPageResponse(1, 100, 1_000, 1, 100, true));
    cache.ApplyScannedPage(BuildPageResponse(4, 100, 1_000, 301, 400, true));

    var projection = new ProjectionEngine().Project(
        new BeatmapRunState
        {
            CurrentModifiedScore = 550000,
            Accuracy = 0.55,
            SongProgressRatio = 0.5,
            ScoredNotes = 160,
            HasRecentNotes = true
        },
        cache,
        useRawPpRank: true);

    Assert(projection.ProjectedRank == 401, $"Broad scanned segments should let projection move below page 4, got {projection.ProjectedRank}.");
    Assert(!projection.ExactRankCovered, "Projection should remain inexact until the containing page is loaded.");
}

static async Task RunRateLimiterTestAsync()
{
    var now = DateTimeOffset.Parse("2026-04-26T00:00:00Z");
    var delayed = TimeSpan.Zero;
    var limiter = new BeatLeaderRateLimiter(
        TimeSpan.FromSeconds(1),
        () => now,
        (delay, _) =>
        {
            delayed += delay;
            now += delay;
            return Task.CompletedTask;
        });

    await limiter.WaitAsync(CancellationToken.None);
    await limiter.WaitAsync(CancellationToken.None);

    Assert(delayed == TimeSpan.FromSeconds(1), $"Rate limiter delayed for {delayed}, expected 1 second.");
}

static async Task RunSessionManagerStartAndPbHintTestAsync()
{
    var apiClient = new FakeBeatLeaderApiClient();
    apiClient.Pages[1] = BuildPageResponse(1, 10, 100, 1, 10, true);
    apiClient.Pages[2] = BuildPageResponse(2, 10, 100, 11, 20, true);
    apiClient.PlayerBest = new BeatLeaderScoreDto
    {
        Id = 99,
        Rank = 15,
        ModifiedScore = 850000,
        Player = new BeatLeaderPlayerDto { Id = "player-local", Name = "Local", AvatarUrl = "/avatars/local.png" }
    };

    var manager = new BeatLeaderOverlaySessionManager(
        apiClient,
        new OverlayStateMachine(new OverlayConfig()),
        new NullOverlayLogger());

    var result = await manager.StartAsync(
        new BeatmapSessionInfo { Hash = "ABCDEF", Difficulty = "ExpertPlus", Mode = "Standard" },
        "player-local",
        CancellationToken.None);

    Assert(result.IsStarted, $"Expected session to start: {result.ErrorMessage}");
    Assert(result.Cache?.FetchedPages.Count == 2, $"Expected page 1 and PB page, got {result.Cache?.FetchedPages.Count}.");
    Assert(apiClient.LeaderboardRequests.Count == 2, "Expected two leaderboard page requests.");
    Assert(apiClient.LeaderboardRequestCounts.SequenceEqual(new[] { 10, 10 }), "Expected BeatLeader pages to be requested in 10-row chunks.");
    Assert(apiClient.PlayerBestRequests == 1, "Expected one player best request.");
    Assert(apiClient.LeaderboardDetailRequests == 0, "Live BeatLeader score-page loading should not issue page-detail avatar fallback requests.");
}

static async Task RunSessionLifecycleRaceTestsAsync()
{
    foreach (var stage in new[] { "first-page", "ratings", "personal-best" })
    foreach (var replacement in new[] { false, true })
    {
        var client = new FakeBeatLeaderApiClient();
        var page = BuildPageResponse(1, 10, 100, 1, 10, true);
        client.Pages[1] = page;
        Action completeOld;
        if (stage == "first-page")
        {
            var pending = new TaskCompletionSource<BeatLeaderApiResult<LeaderboardScoresResponse>>(TaskCreationOptions.RunContinuationsAsynchronously);
            client.PageHandler = _ => pending.Task;
            completeOld = () => pending.SetResult(BeatLeaderApiResult<LeaderboardScoresResponse>.Success(page, HttpStatusCode.OK));
        }
        else if (stage == "ratings")
        {
            var pending = new TaskCompletionSource<BeatLeaderApiResult<BeatLeaderSongResponse>>(TaskCreationOptions.RunContinuationsAsynchronously);
            client.SongHandler = () => pending.Task;
            completeOld = () => pending.SetResult(BeatLeaderApiResult<BeatLeaderSongResponse>.Failure(HttpStatusCode.NotFound, "late response"));
        }
        else
        {
            var pending = new TaskCompletionSource<BeatLeaderApiResult<BeatLeaderScoreDto>>(TaskCreationOptions.RunContinuationsAsynchronously);
            client.BestHandler = () => pending.Task;
            completeOld = () => pending.SetResult(BeatLeaderApiResult<BeatLeaderScoreDto>.Success(new BeatLeaderScoreDto { Rank = 1, ModifiedScore = 1 }, HttpStatusCode.OK));
        }

        var state = new OverlayStateMachine(new OverlayConfig());
        var manager = new BeatLeaderOverlaySessionManager(client, state, new NullOverlayLogger());
        var map = new BeatmapSessionInfo { Hash = "ABCDEF", Difficulty = "ExpertPlus", Mode = "Standard" };
        var old = manager.StartAsync(map, "old-player", CancellationToken.None);
        Assert(!old.IsCompleted, $"Expected {stage} to remain in flight.");
        manager.End();
        client.PageHandler = null;
        client.SongHandler = null;
        client.BestHandler = null;
        OverlaySessionStartResult? current = null;
        if (replacement) current = await manager.StartAsync(map, "new-player", CancellationToken.None);
        var scoreBefore = current?.Cache?.ScoresByRank[1].ModifiedScore;
        completeOld(); // Simulate a server/client that ignores cancellation entirely.
        var cancelled = false;
        try { await old; }
        catch (OperationCanceledException) { cancelled = true; }
        Assert(cancelled, $"An obsolete {stage} completion must be rejected after {(replacement ? "replacement" : "exit")}.");
        if (replacement)
            Assert(current!.IsStarted && current.Cache!.ScoresByRank[1].ModifiedScore == scoreBefore, "A late personal best must not overwrite the replacement cache.");
        else
            Assert(state.Update(new BeatmapRunState()).ViewModel.Mode == OverlayMode.Hidden, "An ended session must stay hidden after a late result.");
    }

    var cancelledClient = new FakeBeatLeaderApiClient();
    var cancelledManager = new BeatLeaderOverlaySessionManager(cancelledClient, new OverlayStateMachine(new OverlayConfig()), new NullOverlayLogger());
    using var cancellation = new CancellationTokenSource();
    cancellation.Cancel();
    try { await cancelledManager.StartAsync(new BeatmapSessionInfo(), null, cancellation.Token); Assert(false, "Cancelled startup must throw."); }
    catch (OperationCanceledException) { }
    Assert(cancelledClient.LeaderboardRequests.Count == 0, "Already-cancelled work must not start a request or clear another session.");

    var fetchClient = new FakeBeatLeaderApiClient();
    fetchClient.Pages[1] = BuildPageResponse(1, 10, 100, 1, 10, true);
    var fetchManager = new BeatLeaderOverlaySessionManager(fetchClient, new OverlayStateMachine(new OverlayConfig()), new NullOverlayLogger());
    var fetchMap = new BeatmapSessionInfo { Hash = "ABCDEF", Difficulty = "ExpertPlus", Mode = "Standard" };
    await fetchManager.StartAsync(fetchMap, null, CancellationToken.None);
    var oldPage = new TaskCompletionSource<BeatLeaderApiResult<LeaderboardScoresResponse>>(TaskCreationOptions.RunContinuationsAsynchronously);
    fetchClient.PageHandler = _ => oldPage.Task;
    var hint = new OverlayUpdateResult(OverlayViewModel.Hidden(), 2);
    var oldFetch = fetchManager.TryFetchSuggestedPageAsync(hint, CancellationToken.None);
    fetchManager.End();
    fetchClient.PageHandler = null;
    await fetchManager.StartAsync(fetchMap, null, CancellationToken.None);
    var newPage = new TaskCompletionSource<BeatLeaderApiResult<LeaderboardScoresResponse>>(TaskCreationOptions.RunContinuationsAsynchronously);
    fetchClient.PageHandler = _ => newPage.Task;
    var newFetch = fetchManager.TryFetchSuggestedPageAsync(hint, CancellationToken.None);
    var reply = BeatLeaderApiResult<LeaderboardScoresResponse>.Success(BuildPageResponse(2, 10, 100, 11, 20, true), HttpStatusCode.OK);
    oldPage.SetResult(reply);
    try { await oldFetch; Assert(false, "Old page response must be rejected even when its cache was reused."); }
    catch (OperationCanceledException) { }
    var duplicate = fetchManager.TryFetchSuggestedPageAsync(hint, CancellationToken.None);
    Assert(duplicate.IsCompleted && !await duplicate, "Old fetch cleanup must not remove the newer request's in-flight guard.");
    newPage.SetResult(reply);
    Assert(await newFetch, "The replacement session's page should still load successfully.");
}

static void RunSessionMainThreadContinuationTest()
{
    var context = new PumpSynchronizationContext();
    var owner = Environment.CurrentManagedThreadId;
    var ratingsThread = -1;
    var bestThread = -1;
    context.Run(async () =>
    {
        var client = new FakeBeatLeaderApiClient
        {
            PageHandler = _ => Task.Run(() => BeatLeaderApiResult<LeaderboardScoresResponse>.Success(BuildPageResponse(1, 10, 100, 1, 10, true), HttpStatusCode.OK)),
            SongHandler = () =>
            {
                ratingsThread = Environment.CurrentManagedThreadId;
                return Task.Run(() => BeatLeaderApiResult<BeatLeaderSongResponse>.Failure(HttpStatusCode.NotFound, "no ratings"));
            },
            BestHandler = () =>
            {
                bestThread = Environment.CurrentManagedThreadId;
                return Task.Run(() => BeatLeaderApiResult<BeatLeaderScoreDto>.Failure(HttpStatusCode.NotFound, "no personal best"));
            }
        };
        var manager = new BeatLeaderOverlaySessionManager(client, new OverlayStateMachine(new OverlayConfig()), new NullOverlayLogger());
        var result = await manager.StartAsync(new BeatmapSessionInfo { Hash = "ABCDEF", Difficulty = "ExpertPlus", Mode = "Standard" }, "local", CancellationToken.None);
        Assert(result.IsStarted, "Startup should survive background I/O completions.");
        Assert(Environment.CurrentManagedThreadId == owner && ratingsThread == owner && bestThread == owner,
            "Session continuations must return to the calling main-thread context after every I/O stage.");
    });
}

static void RunFixedPlayerLabelLifecycleTest()
{
    // Legacy configuration cannot restore a custom label.
    var config = JsonSerializer.Deserialize<OverlayConfig>("{\"DisplayNameOverride\":\"Old custom name\"}")!;
    var stateMachine = new OverlayStateMachine(config);
    var cache = BuildCache();
    cache.ScoresByRank[1].PlayerName = "You";
    stateMachine.StartSession(cache, "p2");
    var run = new BeatmapRunState
    {
        CurrentScore = 900000,
        CurrentModifiedScore = 900000,
        Accuracy = 0.9,
        SongProgressRatio = 0.5,
        HasRecentNotes = true
    };
    var live = stateMachine.Update(run).ViewModel;
    Assert(live.DisplayName == "You", "Live label must be You.");
    Assert(live.Rows.Single(row => row.IsProjected).PlayerName == "You", "Live projected row must be You.");
    Assert(live.Rows.Any(row => !row.IsProjected && row.PlayerId == "p1" && row.PlayerName == "You"),
        "An unrelated leaderboard player named You must not be removed.");

    run.IsReplayMode = true;
    var replay = stateMachine.Update(run).ViewModel;
    Assert(replay.DisplayName == "You", "Late replay detection must keep You.");
    var subject = replay.Rows.Single(row => row.IsProjected);
    Assert(subject.PlayerName == "You" && string.IsNullOrEmpty(subject.PlayerId),
        "Late replay detection must clear viewer ID without changing the label.");

    stateMachine.EndSession();
    stateMachine.StartSession(cache, "p3");
    run.IsReplayMode = false;
    var nextLive = stateMachine.Update(run).ViewModel;
    Assert(nextLive.DisplayName == "You" && nextLive.Rows.Single(row => row.IsProjected).PlayerId == "p3",
        "A new live session must retain You and use its own player ID.");
}

static async Task RunReplayUsesLocalLabelTestsAsync()
{
    await VerifyReplayUsesLocalLabelAsync("BeatLeader");
    await VerifyReplayUsesLocalLabelAsync("ScoreSaber");
}

static async Task VerifyReplayUsesLocalLabelAsync(string sourceName)
{
    var apiClient = new FakeBeatLeaderApiClient
    {
        SourceNameValue = sourceName
    };
    apiClient.Pages[1] = BuildPageResponse(1, 10, 100, 1, 10, true, sourceName);

    var stateMachine = new OverlayStateMachine(new OverlayConfig());
    var manager = new BeatLeaderOverlaySessionManager(apiClient, stateMachine, new NullOverlayLogger());
    var result = await manager.StartAsync(
        new BeatmapSessionInfo
        {
            Hash = "ABCDEF",
            Difficulty = "ExpertPlus",
            Mode = "Standard",
            IsReplayMode = true,
            ReplayScoreId = 12345
        },
        "76561198000000001",
        CancellationToken.None);

    Assert(result.IsStarted, $"Expected {sourceName} replay session to start: {result.ErrorMessage}");
    var update = manager.Update(new BeatmapRunState
    {
        CurrentScore = 900000,
        CurrentModifiedScore = 900000,
        Accuracy = 0.9,
        SongProgressRatio = 0.5,
        HasRecentNotes = true,
        IsReplayMode = true
    });
    Assert(
        update.ViewModel.DisplayName == "You",
        $"Expected {sourceName} replay to use the local 'You' label, got '{update.ViewModel.DisplayName}'.");
    Assert(update.ViewModel.Rows.Single(row => row.IsProjected).PlayerName == "You", "Replay projected row must be You.");
    Assert(apiClient.PlayerBestRequests == 0, "Replay startup must not fetch the viewer's personal best.");
}

static async Task RunSessionManagerUsesScorePagesWithoutAvatarDetailFallbackTestAsync()
{
    var apiClient = new FakeBeatLeaderApiClient();
    apiClient.Pages[1] = BuildPageResponse(1, 10, 25, 1, 10, true);

    var manager = new BeatLeaderOverlaySessionManager(
        apiClient,
        new OverlayStateMachine(new OverlayConfig()),
        new NullOverlayLogger());

    var result = await manager.StartAsync(
        new BeatmapSessionInfo { Hash = "ABCDEF", Difficulty = "ExpertPlus", Mode = "Standard" },
        null,
        CancellationToken.None);

    Assert(result.IsStarted, $"Expected session to start: {result.ErrorMessage}");
    Assert(apiClient.LeaderboardRequests.SequenceEqual(new[] { 1 }), "Expected only the first score page request at startup.");
    Assert(apiClient.LeaderboardRequestCounts.SequenceEqual(new[] { 10 }), "Expected a 10-row BeatLeader score page request.");
    Assert(apiClient.LeaderboardDetailRequests == 0, "Missing BeatLeader avatars should not trigger page-level detail fallback during live page loading.");
}

static async Task RunSessionManagerUnrankedStartsFromBottomTestAsync()
{
    var apiClient = new FakeBeatLeaderApiClient();
    apiClient.Pages[1] = BuildPageResponse(1, 10, 120, 1, 10, false);
    apiClient.Pages[12] = BuildPageResponse(12, 10, 120, 111, 120, false);

    var manager = new BeatLeaderOverlaySessionManager(
        apiClient,
        new OverlayStateMachine(new OverlayConfig()),
        new NullOverlayLogger());

    var result = await manager.StartAsync(
        new BeatmapSessionInfo { Hash = "ABCDEF", Difficulty = "ExpertPlus", Mode = "Standard" },
        null,
        CancellationToken.None);

    Assert(result.IsStarted, "Expected unranked session to start.");
    Assert(apiClient.LeaderboardRequests.SequenceEqual(new[] { 1, 12 }), "Unranked startup should fetch page 1 for metadata, then the bottom page first.");
    Assert(result.Cache?.FetchedPages.Contains(12) == true, "Expected unranked bottom page in cache.");
}

static async Task RunSessionManagerAppliesContainerModifierValuesTestAsync()
{
    var apiClient = new FakeBeatLeaderApiClient();
    using var document = JsonDocument.Parse("""
        {
          "modifierValues": {
            "fs": 0.2
          }
        }
        """);

    apiClient.Pages[1] = new LeaderboardScoresResponse
    {
        Metadata = new LeaderboardMetadata
        {
            Page = 1,
            ItemsPerPage = 3,
            Total = 3
        },
        Container = new LeaderboardContainer
        {
            LeaderboardId = "leaderboard-container-mods",
            Ranked = false,
            MaxScore = 681_142,
            ExtraData = new Dictionary<string, JsonElement>
            {
                ["modifierValues"] = document.RootElement.GetProperty("modifierValues").Clone()
            }
        },
        Data =
        {
            new BeatLeaderScoreDto { Id = 1, Rank = 1, ModifiedScore = 657000, Player = new BeatLeaderPlayerDto { Id = "p1", Name = "A" } },
            new BeatLeaderScoreDto { Id = 2, Rank = 2, ModifiedScore = 656900, Player = new BeatLeaderPlayerDto { Id = "p2", Name = "B" } },
            new BeatLeaderScoreDto { Id = 3, Rank = 3, ModifiedScore = 650897, Player = new BeatLeaderPlayerDto { Id = "p3", Name = "C" } }
        }
    };

    var manager = new BeatLeaderOverlaySessionManager(
        apiClient,
        new OverlayStateMachine(new OverlayConfig()),
        new NullOverlayLogger());

    var result = await manager.StartAsync(
        new BeatmapSessionInfo
        {
            Hash = "ABCDEF",
            Difficulty = "ExpertPlus",
            Mode = "Standard",
            ActiveModifiers = new[] { "Faster Song" }
        },
        null,
        CancellationToken.None);

    Assert(result.IsStarted, "Expected unranked modified session to start.");
    var cache = result.Cache ?? throw new InvalidOperationException("Expected session cache.");
    Assert(Math.Abs(cache.PositiveScoreModifierMultiplier - 1.2d) < 0.0001d, "Expected container FS multiplier to be applied to session cache.");
    Assert(Math.Abs(cache.NegativeScoreModifierMultiplier - 1d) < 0.0001d, "Expected no negative multiplier.");
}

static async Task RunSessionManagerSuggestedPageFetchTestAsync()
{
    var apiClient = new FakeBeatLeaderApiClient();
    apiClient.PagesByRequest[FakeBeatLeaderApiClient.BuildPageRequestKey(1, 10)] = BuildSparsePageForSession(ranked: true);
    apiClient.PagesByRequest[FakeBeatLeaderApiClient.BuildPageRequestKey(1, 100)] = BuildPageResponse(1, 100, 300, 1, 100, true);
    apiClient.Pages[2] = BuildPageResponse(2, 3, 6, 4, 6, true);
    apiClient.Pages[3] = BuildPageResponse(3, 3, 9, 7, 9, true);

    var manager = new BeatLeaderOverlaySessionManager(
        apiClient,
        new OverlayStateMachine(new OverlayConfig()),
        new NullOverlayLogger());

    var start = await manager.StartAsync(
        new BeatmapSessionInfo { Hash = "ABCDEF", Difficulty = "ExpertPlus", Mode = "Standard" },
        null,
        CancellationToken.None);

    Assert(start.IsStarted, "Expected sparse session to start.");

    var runState = new BeatmapRunState
    {
        CurrentModifiedScore = 350000,
        SongProgressRatio = 0.5,
        ScoredNotes = 160,
        HasRecentNotes = true
    };

    _ = manager.Update(runState);
    var update = manager.Update(runState);
    Assert(update.PageToFetch == 1, $"Expected the broad scanner to start with page 1, got {update.PageToFetch}.");
    Assert(update.FetchPageSize == 100, $"Expected scanner to request 100 rows, got {update.FetchPageSize}.");
    Assert(update.MergeFetchedPageAsScan, "Expected scanner page to merge without changing normal page bookkeeping.");

    var fetched = await manager.TryFetchSuggestedPageAsync(update, CancellationToken.None);
    Assert(fetched, "Expected suggested page fetch to apply.");
    Assert(start.Cache?.FetchedPages.Count == 1, "Scanner pages should not be recorded as normal 10-row pages.");
    Assert(start.Cache?.ScoresByRank.ContainsKey(100) == true, "Expected scanner page to merge broad rank coverage.");
}

static async Task RunSessionManagerBackgroundScannerJumpsAfterBroadPageTestAsync()
{
    var apiClient = new FakeBeatLeaderApiClient();
    apiClient.PagesByRequest[FakeBeatLeaderApiClient.BuildPageRequestKey(1, 10)] = BuildPageResponse(1, 10, 1_000, 1, 10, true);
    apiClient.PagesByRequest[FakeBeatLeaderApiClient.BuildPageRequestKey(1, 100)] = BuildPageResponse(1, 100, 1_000, 1, 100, true);
    apiClient.PagesByRequest[FakeBeatLeaderApiClient.BuildPageRequestKey(2, 100)] = BuildPageResponse(2, 100, 1_000, 101, 200, true);
    apiClient.PagesByRequest[FakeBeatLeaderApiClient.BuildPageRequestKey(4, 100)] = BuildPageResponse(4, 100, 1_000, 301, 400, true);

    var manager = new BeatLeaderOverlaySessionManager(
        apiClient,
        new OverlayStateMachine(new OverlayConfig()),
        new NullOverlayLogger());

    var start = await manager.StartAsync(
        new BeatmapSessionInfo { Hash = "ABCDEF", Difficulty = "ExpertPlus", Mode = "Standard" },
        null,
        CancellationToken.None);

    Assert(start.IsStarted, "Expected scanner session to start.");

    var runState = new BeatmapRunState
    {
        CurrentModifiedScore = 550000,
        Accuracy = 0.55,
        SongProgressRatio = 0.5,
        ScoredNotes = 160,
        HasRecentNotes = true
    };

    var firstScan = manager.Update(runState);
    Assert(firstScan.PageToFetch == 1 && firstScan.FetchPageSize == 100 && firstScan.MergeFetchedPageAsScan, "Expected scanner to start with page 1 at 100 rows.");
    Assert(await manager.TryFetchSuggestedPageAsync(firstScan, CancellationToken.None), "Expected first scanner page to fetch.");

    var secondScan = manager.Update(runState);
    Assert(secondScan.PageToFetch == 2 && secondScan.FetchPageSize == 100 && secondScan.MergeFetchedPageAsScan, $"Expected scanner to move to page 2, got page={secondScan.PageToFetch} size={secondScan.FetchPageSize}.");
    Assert(await manager.TryFetchSuggestedPageAsync(secondScan, CancellationToken.None), "Expected second scanner page to fetch.");

    var jumpScan = manager.Update(runState);
    Assert(jumpScan.PageToFetch == 4 && jumpScan.FetchPageSize == 100 && jumpScan.MergeFetchedPageAsScan, $"Expected scanner to jump to page 4 after pages 1 and 2, got page={jumpScan.PageToFetch} size={jumpScan.FetchPageSize}.");
}

static async Task RunSessionManagerRefreshesFetchedPageForMissingVisibleRankTestAsync()
{
    var apiClient = new FakeBeatLeaderApiClient();
    apiClient.Pages[1] = BuildMissingTopRankPage();

    var manager = new BeatLeaderOverlaySessionManager(
        apiClient,
        new OverlayStateMachine(new OverlayConfig { VisiblePlayerCount = 2 }),
        new NullOverlayLogger());

    var start = await manager.StartAsync(
        new BeatmapSessionInfo { Hash = "ABCDEF", Difficulty = "ExpertPlus", Mode = "Standard" },
        "local-player-id",
        CancellationToken.None);

    Assert(start.IsStarted, "Expected missing-top-rank session to start.");

    var runState = new BeatmapRunState
    {
        CurrentModifiedScore = 998500,
        Accuracy = 0.995,
        SongProgressRatio = 0.5,
        ScoredNotes = 160,
        HasRecentNotes = true,
        HasUpcomingScorableNote = true
    };

    _ = manager.Update(runState);
    var update = manager.Update(runState);
    Assert(update.PageToFetch == 1 && update.ForcePageRefresh, $"Expected forced refresh of page 1, got page={update.PageToFetch}, force={update.ForcePageRefresh}.");

    var fetched = await manager.TryFetchSuggestedPageAsync(update, CancellationToken.None);
    Assert(fetched, "Expected forced suggested page refresh to apply even though page 1 was already fetched.");
    Assert(apiClient.LeaderboardRequests.SequenceEqual(new[] { 1, 1 }), "Expected startup page 1 fetch followed by one forced page 1 refresh.");
}

static LeaderboardSessionCache BuildCache()
{
    var response = new LeaderboardScoresResponse
    {
        Metadata = new LeaderboardMetadata
        {
            Page = 1,
            ItemsPerPage = 3,
            Total = 3
        },
        Container = new LeaderboardContainer
        {
            LeaderboardId = "leaderboard-1",
            Ranked = true
        },
        Data =
        {
            new BeatLeaderScoreDto
            {
                Id = 1,
                Rank = 1,
                ModifiedScore = 950000,
                Player = new BeatLeaderPlayerDto { Id = "p1", Name = "Player A" }
            },
            new BeatLeaderScoreDto
            {
                Id = 2,
                Rank = 2,
                ModifiedScore = 880000,
                Player = new BeatLeaderPlayerDto { Id = "p2", Name = "Player B" }
            },
            new BeatLeaderScoreDto
            {
                Id = 3,
                Rank = 3,
                ModifiedScore = 800000,
                Player = new BeatLeaderPlayerDto { Id = "p3", Name = "Player C" }
            }
        }
    };

    var cache = new LeaderboardSessionCache("ABCDEF", "ExpertPlus", "Standard");
    cache.ApplyPage(response);
    return cache;
}

static LeaderboardSessionCache BuildSparseCache()
{
    var response = new LeaderboardScoresResponse
    {
        Metadata = new LeaderboardMetadata
        {
            Page = 1,
            ItemsPerPage = 3,
            Total = 6
        },
        Container = new LeaderboardContainer
        {
            LeaderboardId = "leaderboard-1",
            Ranked = false
        },
        Data =
        {
            new BeatLeaderScoreDto
            {
                Id = 1,
                Rank = 1,
                ModifiedScore = 950000,
                Player = new BeatLeaderPlayerDto { Id = "p1", Name = "Player A" }
            },
            new BeatLeaderScoreDto
            {
                Id = 2,
                Rank = 2,
                ModifiedScore = 880000,
                Player = new BeatLeaderPlayerDto { Id = "p2", Name = "Player B" }
            },
            new BeatLeaderScoreDto
            {
                Id = 3,
                Rank = 3,
                ModifiedScore = 800000,
                Player = new BeatLeaderPlayerDto { Id = "p3", Name = "Player C" }
            },
            new BeatLeaderScoreDto
            {
                Id = 4,
                Rank = 6,
                ModifiedScore = 600000,
                Player = new BeatLeaderPlayerDto { Id = "p6", Name = "Player F" }
            }
        }
    };

    var cache = new LeaderboardSessionCache("ABCDEF", "ExpertPlus", "Standard");
    cache.ApplyPage(response);
    return cache;
}

static LeaderboardSessionCache BuildMissingTopRankCache()
{
    var cache = new LeaderboardSessionCache("ABCDEF", "ExpertPlus", "Standard");
    cache.ApplyPage(BuildMissingTopRankPage());
    return cache;
}

static LeaderboardScoresResponse BuildMissingTopRankPage()
{
    return BuildPageResponse(1, 10, 10, 2, 10, true);
}

static LeaderboardSessionCache BuildUnrankedModifierCache()
{
    var response = new LeaderboardScoresResponse
    {
        Metadata = new LeaderboardMetadata
        {
            Page = 1,
            ItemsPerPage = 3,
            Total = 3
        },
        Container = new LeaderboardContainer
        {
            LeaderboardId = "leaderboard-unranked-modifier",
            Ranked = false,
            MaxScore = 1_000_000
        },
        Data =
        {
            new BeatLeaderScoreDto
            {
                Id = 1,
                Rank = 1,
                ModifiedScore = 760000,
                Player = new BeatLeaderPlayerDto { Id = "p1", Name = "Player A" }
            },
            new BeatLeaderScoreDto
            {
                Id = 2,
                Rank = 2,
                ModifiedScore = 700000,
                Player = new BeatLeaderPlayerDto { Id = "p2", Name = "Player B" }
            },
            new BeatLeaderScoreDto
            {
                Id = 3,
                Rank = 3,
                ModifiedScore = 600000,
                Player = new BeatLeaderPlayerDto { Id = "p3", Name = "Player C" }
            }
        }
    };

    var cache = new LeaderboardSessionCache("ABCDEF", "ExpertPlus", "Standard");
    cache.ApplyPage(response);
    cache.ApplyDifficultyRatings(
        null,
        null,
        null,
        null,
        null,
        modifierMultiplier: 1d,
        scoreModifierMultiplier: 0.8d,
        positiveScoreModifierMultiplier: 1d,
        negativeScoreModifierMultiplier: 0.8d);
    return cache;
}

static LeaderboardSessionCache BuildUnrankedPositiveModifierCache(bool apiSupportsPp = false)
{
    var response = new LeaderboardScoresResponse
    {
        Metadata = new LeaderboardMetadata
        {
            Page = 1,
            ItemsPerPage = 3,
            Total = 3
        },
        Container = new LeaderboardContainer
        {
            LeaderboardId = "leaderboard-unranked-positive-modifier",
            Ranked = false,
            SupportsPp = apiSupportsPp,
            MaxScore = 681_142
        },
        Data =
        {
            new BeatLeaderScoreDto
            {
                Id = 1,
                Rank = 1,
                ModifiedScore = 657000,
                Player = new BeatLeaderPlayerDto { Id = "p1", Name = "Player A" }
            },
            new BeatLeaderScoreDto
            {
                Id = 2,
                Rank = 2,
                ModifiedScore = 656900,
                Player = new BeatLeaderPlayerDto { Id = "p2", Name = "Player B" }
            },
            new BeatLeaderScoreDto
            {
                Id = 3,
                Rank = 3,
                ModifiedScore = 650897,
                Player = new BeatLeaderPlayerDto { Id = "p3", Name = "Player C" }
            }
        }
    };

    var cache = new LeaderboardSessionCache("ABCDEF", "ExpertPlus", "Standard");
    cache.ApplyPage(response);
    cache.ApplyDifficultyRatings(
        null,
        null,
        null,
        null,
        null,
        modifierMultiplier: 1d,
        scoreModifierMultiplier: 1.2d,
        positiveScoreModifierMultiplier: 1.2d,
        negativeScoreModifierMultiplier: 1d);
    return cache;
}

static LeaderboardSessionCache BuildRankedCacheWithPp()
{
    var response = new LeaderboardScoresResponse
    {
        Metadata = new LeaderboardMetadata
        {
            Page = 1,
            ItemsPerPage = 5,
            Total = 5
        },
        Container = new LeaderboardContainer
        {
            LeaderboardId = "leaderboard-raw-pp",
            Ranked = true
        },
        Data =
        {
            new BeatLeaderScoreDto { Id = 1, Rank = 1, ModifiedScore = 980000, Pp = 450, Player = new BeatLeaderPlayerDto { Id = "p1", Name = "A" } },
            new BeatLeaderScoreDto { Id = 2, Rank = 2, ModifiedScore = 940000, Pp = 420, Player = new BeatLeaderPlayerDto { Id = "p2", Name = "B" } },
            new BeatLeaderScoreDto { Id = 3, Rank = 3, ModifiedScore = 900000, Pp = 390, Player = new BeatLeaderPlayerDto { Id = "p3", Name = "C" } },
            new BeatLeaderScoreDto { Id = 4, Rank = 4, ModifiedScore = 860000, Pp = 360, Player = new BeatLeaderPlayerDto { Id = "p4", Name = "D" } },
            new BeatLeaderScoreDto { Id = 5, Rank = 5, ModifiedScore = 820000, Pp = 330, Player = new BeatLeaderPlayerDto { Id = "p5", Name = "E" } }
        }
    };

    var cache = new LeaderboardSessionCache("ABCDEF", "ExpertPlus", "Standard");
    cache.ApplyPage(response);
    cache.ApplyDifficultyRatings(10.5, 0.96, 6.1, 7.4, 3.3);
    return cache;
}

static LeaderboardSessionCache BuildBeatLeaderPpRowsWithFalseRankedFlag()
{
    var response = new LeaderboardScoresResponse
    {
        Metadata = new LeaderboardMetadata
        {
            Page = 1,
            ItemsPerPage = 3,
            Total = 3
        },
        Container = new LeaderboardContainer
        {
            LeaderboardId = "beatleader-ranked-pp-false-flag",
            Ranked = false,
            SourceName = "BeatLeader",
            SupportsPp = true,
            RankByPp = true,
            Stars = 10.0,
            MaxScore = 1_000_000
        },
        Data =
        {
            new BeatLeaderScoreDto { Id = 1, Rank = 1, ModifiedScore = 980000, Accuracy = 0.98, Pp = 600, Player = new BeatLeaderPlayerDto { Id = "top", Name = "Top" } },
            new BeatLeaderScoreDto { Id = 2, Rank = 2, ModifiedScore = 940000, Accuracy = 0.94, Pp = 500, Player = new BeatLeaderPlayerDto { Id = "second", Name = "Second" } },
            new BeatLeaderScoreDto { Id = 3, Rank = 3, ModifiedScore = 900000, Accuracy = 0.90, Pp = 400, Player = new BeatLeaderPlayerDto { Id = "third", Name = "Third" } }
        }
    };

    var cache = new LeaderboardSessionCache("ABCDEF", "ExpertPlus", "Standard");
    cache.ApplyPage(response);
    cache.ApplyDifficultyRatings(10.0, 0.965, 10.0, 10.0, 4.0);
    return cache;
}

static LeaderboardSessionCache BuildScoreSaberAccuracyCacheWithLaterPlayerScore()
{
    var cache = new LeaderboardSessionCache("ABCDEF", "ExpertPlus", "Standard");
    cache.ApplyPage(new LeaderboardScoresResponse
    {
        Metadata = new LeaderboardMetadata
        {
            Page = 1,
            ItemsPerPage = 10,
            Total = 700
        },
        Container = new LeaderboardContainer
        {
            LeaderboardId = "scoresaber-accuracy",
            Ranked = false,
            SourceName = "ScoreSaber",
            RankByPp = false,
            SupportsPp = false,
            MaxScore = 1_000_000
        },
        Data =
        {
            new BeatLeaderScoreDto { Id = 1, Rank = 1, ModifiedScore = 995000, Accuracy = 0.995, Player = new BeatLeaderPlayerDto { Id = "top", Name = "Top Player", AvatarUrl = "https://scoresaber.com/avatars/top.png" } },
            new BeatLeaderScoreDto { Id = 2, Rank = 2, ModifiedScore = 990000, Accuracy = 0.990, Player = new BeatLeaderPlayerDto { Id = "second", Name = "Second Player", AvatarUrl = "https://scoresaber.com/avatars/second.png" } },
            new BeatLeaderScoreDto { Id = 3, Rank = 3, ModifiedScore = 985000, Accuracy = 0.985, Player = new BeatLeaderPlayerDto { Id = "third", Name = "Third Player", AvatarUrl = "https://scoresaber.com/avatars/third.png" } }
        }
    });
    cache.ApplyPage(new LeaderboardScoresResponse
    {
        Metadata = new LeaderboardMetadata
        {
            Page = 70,
            ItemsPerPage = 10,
            Total = 700
        },
        Container = new LeaderboardContainer
        {
            LeaderboardId = "scoresaber-accuracy",
            Ranked = false,
            SourceName = "ScoreSaber",
            RankByPp = false,
            SupportsPp = false,
            MaxScore = 1_000_000
        },
        Data =
        {
            new BeatLeaderScoreDto { Id = 700, Rank = 700, ModifiedScore = 400000, Accuracy = 0.400, Player = new BeatLeaderPlayerDto { Id = "local", Name = "Local", AvatarUrl = "https://scoresaber.com/avatars/local.png" } }
        }
    });

    return cache;
}

static LeaderboardSessionCache BuildSparseRankedCacheWithGap()
{
    var response = new LeaderboardScoresResponse
    {
        Metadata = new LeaderboardMetadata
        {
            Page = 1,
            ItemsPerPage = 3,
            Total = 10
        },
        Container = new LeaderboardContainer
        {
            LeaderboardId = "leaderboard-gap",
            Ranked = true
        },
        Data =
        {
            new BeatLeaderScoreDto { Id = 1, Rank = 1, ModifiedScore = 980000, Pp = 500, Player = new BeatLeaderPlayerDto { Id = "p1", Name = "A" } },
            new BeatLeaderScoreDto { Id = 2, Rank = 2, ModifiedScore = 960000, Pp = 480, Player = new BeatLeaderPlayerDto { Id = "p2", Name = "B" } },
            new BeatLeaderScoreDto { Id = 3, Rank = 3, ModifiedScore = 940000, Pp = 460, Player = new BeatLeaderPlayerDto { Id = "p3", Name = "C" } },
            new BeatLeaderScoreDto { Id = 10, Rank = 10, ModifiedScore = 750000, Pp = 300, Player = new BeatLeaderPlayerDto { Id = "p10", Name = "J" } }
        }
    };

    var cache = new LeaderboardSessionCache("ABCDEF", "ExpertPlus", "Standard");
    cache.ApplyPage(response);
    cache.ApplyDifficultyRatings(10.5, 0.96, 6.1, 7.4, 3.3);
    return cache;
}

static LeaderboardSessionCache BuildRankedCacheWithKnownLaterPage()
{
    var cache = new LeaderboardSessionCache("ABCDEF", "ExpertPlus", "Standard");
    cache.ApplyPage(BuildPageResponse(1, 100, 300, 1, 100, true));
    cache.ApplyPage(BuildPageResponse(2, 100, 300, 201, 300, true));
    cache.ApplyDifficultyRatings(10.5, 0.96, 6.1, 7.4, 3.3);
    return cache;
}

static LeaderboardSessionCache BuildRankedPpComparisonCache()
{
    var response = new LeaderboardScoresResponse
    {
        Metadata = new LeaderboardMetadata
        {
            Page = 1,
            ItemsPerPage = 4,
            Total = 4
        },
        Container = new LeaderboardContainer
        {
            LeaderboardId = "leaderboard-pp-compare",
            Ranked = true
        },
        Data =
        {
            new BeatLeaderScoreDto { Id = 1, Rank = 1, ModifiedScore = 980000, Pp = 600, Player = new BeatLeaderPlayerDto { Id = "p1", Name = "A" } },
            new BeatLeaderScoreDto { Id = 2, Rank = 2, ModifiedScore = 900000, Pp = 550, Player = new BeatLeaderPlayerDto { Id = "p2", Name = "B" } },
            new BeatLeaderScoreDto { Id = 3, Rank = 3, ModifiedScore = 500000, Pp = 250, Player = new BeatLeaderPlayerDto { Id = "p3", Name = "C" } },
            new BeatLeaderScoreDto { Id = 4, Rank = 4, ModifiedScore = 480000, Pp = 240, Player = new BeatLeaderPlayerDto { Id = "p4", Name = "D" } }
        }
    };

    var cache = new LeaderboardSessionCache("ABCDEF", "ExpertPlus", "Standard");
    cache.ApplyPage(response);
    return cache;
}

static LeaderboardScoresResponse BuildSparsePageForSession(bool ranked = false)
{
    return new LeaderboardScoresResponse
    {
        Metadata = new LeaderboardMetadata
        {
            Page = 1,
            ItemsPerPage = 3,
            Total = 6
        },
        Container = new LeaderboardContainer
        {
            LeaderboardId = "leaderboard-1",
            Ranked = ranked
        },
        Data =
        {
            new BeatLeaderScoreDto
            {
                Id = 1,
                Rank = 1,
                ModifiedScore = 950000,
                Player = new BeatLeaderPlayerDto { Id = "p1", Name = "Player A" }
            },
            new BeatLeaderScoreDto
            {
                Id = 2,
                Rank = 2,
                ModifiedScore = 880000,
                Player = new BeatLeaderPlayerDto { Id = "p2", Name = "Player B" }
            },
            new BeatLeaderScoreDto
            {
                Id = 3,
                Rank = 3,
                ModifiedScore = 800000,
                Player = new BeatLeaderPlayerDto { Id = "p3", Name = "Player C" }
            },
            new BeatLeaderScoreDto
            {
                Id = 6,
                Rank = 6,
                ModifiedScore = 600000,
                Player = new BeatLeaderPlayerDto { Id = "p6", Name = "Player F" }
            }
        }
    };
}

static LeaderboardScoresResponse BuildPageResponse(
    int page,
    int itemsPerPage,
    int total,
    int startRank,
    int endRank,
    bool ranked,
    string sourceName = "BeatLeader")
{
    var response = new LeaderboardScoresResponse
    {
        Metadata = new LeaderboardMetadata
        {
            Page = page,
            ItemsPerPage = itemsPerPage,
            Total = total
        },
        Container = new LeaderboardContainer
        {
            LeaderboardId = "leaderboard-1",
            Ranked = ranked,
            SourceName = sourceName,
            RankByPp = !string.Equals(sourceName, "ScoreSaber", StringComparison.OrdinalIgnoreCase),
            SupportsPp = ranked,
            UsesScoreSaberPpCurve = string.Equals(sourceName, "ScoreSaber", StringComparison.OrdinalIgnoreCase)
        }
    };

    for (var rank = startRank; rank <= endRank; rank++)
    {
        response.Data.Add(new BeatLeaderScoreDto
        {
            Id = rank,
            Rank = rank,
            ModifiedScore = Math.Max(1, 1_000_000 - (rank * 1_000)),
            Pp = ranked ? Math.Max(0, 500 - rank) : null,
            Player = new BeatLeaderPlayerDto
            {
                Id = "p" + rank,
                Name = "Player " + rank
            }
        });
    }

    return response;
}

static string CreateTestRoot()
{
    return Path.Combine(Path.GetTempPath(), "BeatRelaySmokeTest", Guid.NewGuid().ToString("N"));
}

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

internal sealed class FakeHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> responder;

    public FakeHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        this.responder = responder;
    }

    public int RequestCount { get; private set; }

    public Uri? LastRequestUri { get; private set; }

    public List<Uri> RequestUris { get; } = new();

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        RequestCount++;
        LastRequestUri = request.RequestUri;
        if (request.RequestUri != null)
        {
            RequestUris.Add(request.RequestUri);
        }

        return Task.FromResult(responder(request));
    }
}

internal sealed class FakeBeatLeaderApiClient : IBeatLeaderApiClient
{
    public Func<int, Task<BeatLeaderApiResult<LeaderboardScoresResponse>>>? PageHandler { get; set; }
    public Func<Task<BeatLeaderApiResult<BeatLeaderSongResponse>>>? SongHandler { get; set; }
    public Func<Task<BeatLeaderApiResult<BeatLeaderScoreDto>>>? BestHandler { get; set; }
    public string SourceNameValue { get; set; } = "BeatLeader";

    public string SourceName => SourceNameValue;

    public Dictionary<int, LeaderboardScoresResponse> Pages { get; } = new();

    public Dictionary<string, LeaderboardScoresResponse> PagesByRequest { get; } = new();

    public BeatLeaderScoreDto? PlayerBest { get; set; }

    public List<int> LeaderboardRequests { get; } = new();

    public List<int> LeaderboardRequestCounts { get; } = new();

    public int PlayerBestRequests { get; private set; }


    public int LeaderboardDetailRequests { get; private set; }

    public Task<BeatLeaderApiResult<BeatLeaderOAuthIdentityDto>> GetOAuthIdentityAsync(
        string accessToken,
        CancellationToken cancellationToken)
    {
        return Task.FromResult(BeatLeaderApiResult<BeatLeaderOAuthIdentityDto>.Failure(HttpStatusCode.Unauthorized, "missing identity"));
    }

    public Task<BeatLeaderApiResult<LeaderboardScoresResponse>> GetLeaderboardScoresAsync(
        string hash,
        string difficulty,
        string mode,
        int page,
        int count,
        CancellationToken cancellationToken)
    {
        LeaderboardRequests.Add(page);
        LeaderboardRequestCounts.Add(count);
        if (PageHandler != null) return PageHandler(page);
        return Task.FromResult((PagesByRequest.TryGetValue(BuildPageRequestKey(page, count), out var response)
                || Pages.TryGetValue(page, out response))
            ? BeatLeaderApiResult<LeaderboardScoresResponse>.Success(response, HttpStatusCode.OK)
            : BeatLeaderApiResult<LeaderboardScoresResponse>.Failure(HttpStatusCode.NotFound, "missing page"));
    }

    public static string BuildPageRequestKey(int page, int count)
    {
        return page.ToString() + ":" + count.ToString();
    }

    public Task<BeatLeaderApiResult<BeatLeaderSongResponse>> GetSongByHashAsync(
        string hash,
        CancellationToken cancellationToken)
    {
        if (SongHandler != null) return SongHandler();
        return Task.FromResult(BeatLeaderApiResult<BeatLeaderSongResponse>.Failure(HttpStatusCode.NotFound, "missing map"));
    }

    public Task<BeatLeaderApiResult<LeaderboardDetailResponse>> GetLeaderboardDetailPageAsync(
        string leaderboardId,
        int page,
        int count,
        CancellationToken cancellationToken)
    {
        LeaderboardDetailRequests++;
        return Task.FromResult(BeatLeaderApiResult<LeaderboardDetailResponse>.Failure(HttpStatusCode.NotFound, "not implemented"));
    }

    public Task<BeatLeaderApiResult<BeatLeaderScoreDto>> GetPlayerBestAsync(
        string leaderboardContext,
        string playerId,
        string hash,
        string difficulty,
        string mode,
        CancellationToken cancellationToken)
    {
        PlayerBestRequests++;
        if (BestHandler != null) return BestHandler();
        return Task.FromResult(PlayerBest == null
            ? BeatLeaderApiResult<BeatLeaderScoreDto>.Failure(HttpStatusCode.NotFound, "missing pb")
            : BeatLeaderApiResult<BeatLeaderScoreDto>.Success(PlayerBest, HttpStatusCode.OK));
    }
}

internal sealed class NullOverlayLogger : IOverlayLogger
{
    public void Info(string eventName, string message)
    {
    }

    public void Warn(string eventName, string message)
    {
    }

    public void Error(string eventName, string message)
    {
    }
}

internal sealed class PumpSynchronizationContext : SynchronizationContext
{
    private readonly System.Collections.Concurrent.BlockingCollection<(SendOrPostCallback Callback, object? State)> queue = new();
    public override void Post(SendOrPostCallback callback, object? state) => queue.Add((callback, state));
    public void Run(Func<Task> action)
    {
        var previous = Current;
        SetSynchronizationContext(this);
        try
        {
            var task = action();
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (!task.IsCompleted)
            {
                if (DateTime.UtcNow > deadline) throw new TimeoutException("Main-thread continuation test timed out.");
                if (queue.TryTake(out var work, 50)) work.Callback(work.State);
            }
            task.GetAwaiter().GetResult();
        }
        finally { SetSynchronizationContext(previous); }
    }
}
