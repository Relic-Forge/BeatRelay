#if NETFRAMEWORK
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BeatRelay.Diagnostics;
using HarmonyLib;

namespace BeatRelay.BeatSaber;

public sealed class BeatSaberSessionLogger
{
    private readonly IOverlayLogger logger;
    private Harmony? harmony;

    public BeatSaberSessionLogger(IOverlayLogger logger)
    {
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public void Install()
    {
        if (harmony != null)
        {
            return;
        }

        harmony = new Harmony("com.relicforge.beatrelay.sessionlogger");
        var setupTypes = new[]
        {
            "StandardLevelScenesTransitionSetupDataSO",
            "MissionLevelScenesTransitionSetupDataSO",
            "TutorialScenesTransitionSetupDataSO",
            "MultiplayerLevelScenesTransitionSetupDataSO"
        };

        var patchedSession = false;
        foreach (var setupType in setupTypes)
        {
            patchedSession |= TryPatchInit(setupType);
        }

        var patchedGameplay = TryPatchGameplayHooks();
        logger.Info(
            "session_hooks_install",
            $"standardLevel={patchedSession}; gameplay={patchedGameplay}");
    }

    public void Uninstall()
    {
        if (harmony == null)
        {
            return;
        }

        harmony.UnpatchSelf();
        harmony = null;
        logger.Info("session_hooks_uninstall", "Removed session hooks.");
    }

    private bool TryPatchInit(string typeName)
    {
        var type = AccessTools.TypeByName(typeName);
        if (type == null)
        {
            return false;
        }

        var postfix = AccessTools.Method(typeof(BeatSaberSessionLogger), nameof(OnLevelInitPostfix));
        var methods = AccessTools.GetDeclaredMethods(type)
            .Where(method => string.Equals(method.Name, "Init", StringComparison.Ordinal))
            .ToList();

        foreach (var method in methods)
        {
            harmony!.Patch(method, postfix: new HarmonyMethod(postfix));
        }

        return methods.Count > 0;
    }

    private static void OnLevelInitPostfix(object __instance)
    {
        Plugin.Instance?.SessionLogger?.LogLevelInit(__instance);
    }

    private bool TryPatchGameplayHooks()
    {
        var patched = false;
        var scoreController = AccessTools.TypeByName("ScoreController");
        if (scoreController != null)
        {
            patched |= TryPatchMethod(scoreController, "LateUpdate", nameof(OnScoreControllerLateUpdatePostfix));
            patched |= TryPatchMethod(scoreController, "OnDestroy", nameof(OnScoreControllerDestroyedPostfix));
        }

        var gamePause = AccessTools.TypeByName("GamePause");
        if (gamePause != null)
        {
            patched |= TryPatchMethod(gamePause, "Pause", nameof(OnPausePostfix));
            patched |= TryPatchMethod(gamePause, "Resume", nameof(OnResumePostfix));
        }

        return patched;
    }

    private bool TryPatchMethod(Type type, string methodName, string postfixName)
    {
        var postfix = AccessTools.Method(typeof(BeatSaberSessionLogger), postfixName);
        var methods = AccessTools.GetDeclaredMethods(type)
            .Where(method => string.Equals(method.Name, methodName, StringComparison.Ordinal))
            .ToList();

        foreach (var method in methods)
        {
            harmony!.Patch(method, postfix: new HarmonyMethod(postfix));
        }

        return methods.Count > 0;
    }

    private static void OnScoreControllerLateUpdatePostfix(object __instance)
    {
        Plugin.Instance?.RuntimeCoordinator?.RecordScoreControllerUpdate(__instance);
    }

    private static void OnPausePostfix()
    {
        Plugin.Instance?.RuntimeCoordinator?.SetPaused(true);
    }

    private static void OnResumePostfix()
    {
        Plugin.Instance?.RuntimeCoordinator?.SetPaused(false);
    }

    private static void OnScoreControllerDestroyedPostfix()
    {
        Plugin.Instance?.RuntimeCoordinator?.HandleGameplayExit("score_controller_destroyed");
    }

    private void LogLevelInit(object setup)
    {
        try
        {
            var metadata = ExtractMetadata(setup);
            logger.Info("song_session_detected", metadata.ToLogMessage());
            Plugin.Instance?.RuntimeCoordinator?.StartDetectedSong(metadata.ToBeatmapSessionInfo());
        }
        catch (Exception ex)
        {
            logger.Error("song_session_detect_failed", ex.ToString());
        }
    }

    private static BeatSaberSessionMetadata ExtractMetadata(object setup)
    {
        var gameplaySetupData = ReadMember(setup, "gameplayCoreSceneSetupData", "_gameplayCoreSceneSetupData");
        var beatmapKey = ReadMember(setup, "beatmapKey", "_beatmapKey")
            ?? ReadMember(gameplaySetupData, "beatmapKey", "_beatmapKey");
        var beatmapLevel = ReadMember(setup, "beatmapLevel", "_beatmapLevel")
            ?? ReadMember(setup, "previewBeatmapLevel", "_previewBeatmapLevel")
            ?? ReadMember(gameplaySetupData, "beatmapLevel", "_beatmapLevel")
            ?? ReadMember(gameplaySetupData, "previewBeatmapLevel", "_previewBeatmapLevel");
        var characteristic = ReadMember(beatmapKey, "beatmapCharacteristic", "_beatmapCharacteristic");
        var gameplayModifiers = ReadMember(setup, "gameplayModifiers", "_gameplayModifiers")
            ?? ReadMember(gameplaySetupData, "gameplayModifiers", "_gameplayModifiers");
        var levelScenesTransitionSetupData = ReadMember(gameplaySetupData, "levelScenesTransitionSetupData", "_levelScenesTransitionSetupData");
        var standardGameplaySceneSetupData = ReadMember(levelScenesTransitionSetupData, "standardGameplaySceneSetupData", "_standardGameplaySceneSetupData");
        var playerSpecificSettings =
            ReadMember(setup, "playerSpecificSettings", "_playerSpecificSettings")
            ?? ReadMember(gameplaySetupData, "playerSpecificSettings", "_playerSpecificSettings")
            ?? ReadMember(levelScenesTransitionSetupData, "playerSpecificSettings", "_playerSpecificSettings")
            ?? ReadMember(standardGameplaySceneSetupData, "playerSpecificSettings", "_playerSpecificSettings");
        object? beatmapData = null;
        var levelId = ReadString(beatmapLevel, "levelID", "levelId", "_levelID", "_levelId") ?? string.Empty;
        var songLength = TryReadDouble(beatmapLevel, "songDuration", "_songDuration", "songTimeOffset", "_songTimeOffset");
        var beatsPerMinute = TryReadDouble(beatmapLevel, "beatsPerMinute", "_beatsPerMinute", "bpm", "_bpm");
        var noteTimes = Array.Empty<double>();
        var isPracticeMode = ReadMember(gameplaySetupData, "practiceSettings", "_practiceSettings") != null;
        var replayContexts = BuildReplayContextCandidates(setup, gameplaySetupData);
        var replayScoreId = TryResolveReplayScoreId(replayContexts);
        var isReplayMode = IsReplaySetup(setup, gameplaySetupData, replayContexts)
            || replayScoreId.GetValueOrDefault() > 0;

        return new BeatSaberSessionMetadata
        {
            SetupType = setup.GetType().FullName ?? setup.GetType().Name,
            Hash = ExtractHash(levelId, beatmapLevel),
            Difficulty = ReadSimpleValue(beatmapKey, "difficulty", "_difficulty"),
            Mode = ReadString(characteristic, "serializedName", "_serializedName")
                ?? ReadString(characteristic, "compoundIdPartName", "_compoundIdPartName")
                ?? string.Empty,
            SongTitle = ReadString(beatmapLevel, "songName", "_songName") ?? string.Empty,
            SongAuthor = ReadString(beatmapLevel, "songAuthorName", "_songAuthorName") ?? string.Empty,
            Mapper = ReadString(beatmapLevel, "levelAuthorName", "_levelAuthorName") ?? string.Empty,
            LevelId = levelId,
            ActiveModifiers = ReadGameplayModifiers(gameplayModifiers),
            NoteTimes = noteTimes,
            RuntimeBeatmapData = beatmapData,
            SongLengthSeconds = songLength,
            BeatsPerMinute = beatsPerMinute,
            IsReplayMode = isReplayMode,
            IsPracticeMode = isPracticeMode,
            HudSuppressedByGame = IsNoTextsAndHudsSettingEnabled(playerSpecificSettings) || IsNoTextsAndHudsSettingEnabled(gameplayModifiers),
            ReplayScoreId = replayScoreId
        };
    }

    private static bool IsReplaySetup(object setup, object? gameplaySetupData, IReadOnlyList<object> replayContexts)
    {
        foreach (var context in replayContexts)
        {
            var typeName = context.GetType().Name;
            if (typeName.IndexOf("Replay", StringComparison.OrdinalIgnoreCase) >= 0
                || typeName.IndexOf("BSOR", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }

            var fullName = context.GetType().FullName ?? string.Empty;
            if (fullName.IndexOf("Replay", StringComparison.OrdinalIgnoreCase) >= 0
                || fullName.IndexOf("BSOR", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }

            if (ReadBool(context, "isStartedAsReplay", "IsStartedAsReplay", "_isStartedAsReplay") == true)
            {
                return true;
            }
        }

        return ReadMember(setup, "replayData", "_replayData", "replay", "_replay", "replayMetaData", "_replayMetaData", "replayMetadata", "_replayMetadata", "lastPlayedReplay", "LastPlayedReplay", "mainReplay", "MainReplay") != null
            || ReadMember(gameplaySetupData, "replayData", "_replayData", "replay", "_replay", "replayMetaData", "_replayMetaData", "replayMetadata", "_replayMetadata", "lastPlayedReplay", "LastPlayedReplay", "mainReplay", "MainReplay") != null;
    }

    private static IReadOnlyList<object> BuildReplayContextCandidates(object setup, object? gameplaySetupData)
    {
        var candidates = new List<object> { setup };
        if (gameplaySetupData != null)
        {
            candidates.Add(gameplaySetupData);
        }

        var replayMembers = new[]
        {
            ReadMember(setup, "replayData", "_replayData"),
            ReadMember(setup, "replay", "_replay"),
            ReadMember(setup, "replayMetaData", "_replayMetaData"),
            ReadMember(setup, "replayMetadata", "_replayMetadata"),
            ReadMember(setup, "playerData", "_playerData"),
            ReadMember(setup, "player", "_player"),
            ReadMember(setup, "score", "_score"),
            ReadMember(setup, "resultsData", "_resultsData"),
            ReadMember(gameplaySetupData, "replayData", "_replayData"),
            ReadMember(gameplaySetupData, "replay", "_replay"),
            ReadMember(gameplaySetupData, "playerData", "_playerData"),
            ReadMember(gameplaySetupData, "player", "_player")
        };

        foreach (var replayMember in replayMembers)
        {
            if (replayMember != null)
            {
                candidates.Add(replayMember);
            }
        }

        return candidates;
    }

    private static long? TryResolveReplayScoreId(IReadOnlyList<object> contexts)
    {
        foreach (var context in contexts)
        {
            var direct = TryReadLong(
                context,
                "scoreId",
                "_scoreId",
                "replayScoreId",
                "_replayScoreId",
                "id",
                "_id");
            if (direct.HasValue && direct.Value > 0)
            {
                return direct.Value;
            }

            var nestedScore = ReadMember(context, "score", "_score");
            var nestedId = TryReadLong(nestedScore, "id", "_id", "scoreId", "_scoreId");
            if (nestedId.HasValue && nestedId.Value > 0)
            {
                return nestedId.Value;
            }
        }

        return null;
    }

    private static object? ReadMember(object? target, params string[] names)
    {
        if (target == null)
        {
            return null;
        }

        const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        var type = target.GetType();
        foreach (var name in names)
        {
            var property = type.GetProperty(name, Flags);
            if (property != null)
            {
                return property.GetValue(target);
            }

            var field = type.GetField(name, Flags);
            if (field != null)
            {
                return field.GetValue(target);
            }
        }

        return null;
    }

    private static string? ReadString(object? target, params string[] names)
    {
        var value = ReadMember(target, names);
        return value switch
        {
            null => null,
            string text => text.Trim(),
            _ => value.ToString()
        };
    }

    private static bool? ReadBool(object? target, params string[] names)
    {
        var value = ReadMember(target, names);
        if (value == null)
        {
            return null;
        }

        try
        {
            return Convert.ToBoolean(value);
        }
        catch
        {
            return null;
        }
    }

    private static string ReadSimpleValue(object? target, params string[] names)
    {
        return ReadMember(target, names)?.ToString() ?? string.Empty;
    }

    private static double? TryReadDouble(object? target, params string[] names)
    {
        var value = ReadMember(target, names);
        if (value == null)
        {
            return null;
        }

        try
        {
            return Convert.ToDouble(value);
        }
        catch
        {
            return null;
        }
    }

    private static IReadOnlyList<double> ExtractBeatmapNoteTimes(object? beatmapData)
    {
        var results = new List<double>();
        CollectBeatmapNoteTimes(beatmapData, results, new HashSet<object>(), depth: 0);
        return results
            .Where(time => time >= 0)
            .Distinct()
            .OrderBy(time => time)
            .ToList();
    }

    private static object? FindBeatmapDataObject(params object?[] roots)
    {
        foreach (var root in roots)
        {
            var result = FindBeatmapDataObject(root, new HashSet<object>(), depth: 0);
            if (result != null)
            {
                return result;
            }
        }

        return null;
    }

    private static long? TryReadLong(object? target, params string[] names)
    {
        var value = ReadMember(target, names);
        if (value == null)
        {
            return null;
        }

        try
        {
            return Convert.ToInt64(value);
        }
        catch
        {
            return null;
        }
    }

    private static object? FindBeatmapDataObject(object? source, ISet<object> visited, int depth)
    {
        if (source == null || depth > 5)
        {
            return null;
        }

        if (source is string || source.GetType().IsPrimitive || !visited.Add(source))
        {
            return null;
        }

        if (ReadMember(source, "allBeatmapDataItems", "_allBeatmapDataItems") != null)
        {
            return source;
        }

        if (source is IEnumerable enumerable)
        {
            var inspected = 0;
            foreach (var item in enumerable)
            {
                var found = FindBeatmapDataObject(item, visited, depth + 1);
                if (found != null)
                {
                    return found;
                }

                inspected++;
                if (inspected >= 2000)
                {
                    break;
                }
            }

            return null;
        }

        const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        foreach (var property in source.GetType().GetProperties(Flags))
        {
            if (property.GetIndexParameters().Length != 0 || !ShouldInspectBeatmapMember(property.Name))
            {
                continue;
            }

            try
            {
                var found = FindBeatmapDataObject(property.GetValue(source), visited, depth + 1);
                if (found != null)
                {
                    return found;
                }
            }
            catch
            {
            }
        }

        foreach (var field in source.GetType().GetFields(Flags))
        {
            if (!ShouldInspectBeatmapMember(field.Name))
            {
                continue;
            }

            try
            {
                var found = FindBeatmapDataObject(field.GetValue(source), visited, depth + 1);
                if (found != null)
                {
                    return found;
                }
            }
            catch
            {
            }
        }

        return null;
    }

    private static void CollectBeatmapNoteTimes(object? source, ICollection<double> results, ISet<object> visited, int depth)
    {
        if (source == null || depth > 4 || results.Count > 20000)
        {
            return;
        }

        if (source is string || source.GetType().IsPrimitive || !visited.Add(source))
        {
            return;
        }

        var typeName = source.GetType().Name;
        var lowerTypeName = typeName.ToLowerInvariant();
        if ((lowerTypeName.Contains("note") || lowerTypeName.Contains("beatevent") || lowerTypeName.Contains("beatmapobject"))
            && TryReadDouble(source, "time", "_time", "beat", "_beat") is { } noteTime
            && ShouldCountForPredictiveBreak(source, lowerTypeName))
        {
            results.Add(noteTime);
            return;
        }

        if (source is IEnumerable enumerable)
        {
            var inspected = 0;
            foreach (var item in enumerable)
            {
                CollectBeatmapNoteTimes(item, results, visited, depth + 1);
                inspected++;
                if (inspected >= 25000)
                {
                    break;
                }
            }

            return;
        }

        const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        foreach (var property in source.GetType().GetProperties(Flags))
        {
            if (property.GetIndexParameters().Length != 0 || !ShouldInspectBeatmapMember(property.Name))
            {
                continue;
            }

            try
            {
                CollectBeatmapNoteTimes(property.GetValue(source), results, visited, depth + 1);
            }
            catch
            {
            }
        }

        foreach (var field in source.GetType().GetFields(Flags))
        {
            if (!ShouldInspectBeatmapMember(field.Name))
            {
                continue;
            }

            try
            {
                CollectBeatmapNoteTimes(field.GetValue(source), results, visited, depth + 1);
            }
            catch
            {
            }
        }
    }

    private static bool ShouldInspectBeatmapMember(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        var lower = name.ToLowerInvariant();
        return lower.Contains("note")
            || lower.Contains("beatmap")
            || lower.Contains("object")
            || lower.Contains("item")
            || lower.Contains("data");
    }

    private static bool ShouldCountForPredictiveBreak(object beatmapObject, string lowerTypeName)
    {
        if (lowerTypeName.Contains("obstacle") || lowerTypeName.Contains("wall"))
        {
            return false;
        }

        if (lowerTypeName.Contains("bomb"))
        {
            return false;
        }

        if (!lowerTypeName.Contains("note"))
        {
            return false;
        }

        if (TryReadInt(beatmapObject, "colorType", "_colorType", "type", "_type") is { } noteType && noteType >= 0 && noteType <= 1)
        {
            return true;
        }

        if (TryReadInt(beatmapObject, "scoringType", "_scoringType", "gameplayType", "_gameplayType") is { } scoringType && scoringType == 0)
        {
            return true;
        }

        return !lowerTypeName.Contains("bomb");
    }

    private static bool IsLikelyHitPathBomb(object beatmapObject)
    {
        var line = TryReadInt(beatmapObject, "lineIndex", "_lineIndex", "x", "_x");
        if (!line.HasValue)
        {
            return false;
        }

        return line.Value is >= 1 and <= 2;
    }

    private static bool IsLikelyHitPathObstacle(object beatmapObject)
    {
        var line = TryReadInt(beatmapObject, "lineIndex", "_lineIndex", "line", "_line", "x", "_x") ?? 0;
        var width = TryReadInt(beatmapObject, "width", "_width", "lineSpan", "_lineSpan") ?? 1;
        var end = line + Math.Max(1, width) - 1;
        return line <= 2 && end >= 1;
    }

    private static int? TryReadInt(object? target, params string[] names)
    {
        var value = ReadMember(target, names);
        if (value == null)
        {
            return null;
        }

        try
        {
            return Convert.ToInt32(value);
        }
        catch
        {
            return null;
        }
    }

    private static string ExtractHash(string levelId, object? beatmapLevel)
    {
        var explicitHash = ReadString(beatmapLevel, "hash", "_hash", "levelHash", "_levelHash");
        if (!string.IsNullOrWhiteSpace(explicitHash))
        {
            return explicitHash.Trim().ToLowerInvariant();
        }

        var normalizedLevelId = levelId?.Trim() ?? string.Empty;
        var hashCandidate = new string(normalizedLevelId.Where(char.IsLetterOrDigit).ToArray());
        if (hashCandidate.Length >= 40)
        {
            var tail = hashCandidate.Substring(hashCandidate.Length - 40);
            if (tail.All(Uri.IsHexDigit))
            {
                return tail.ToLowerInvariant();
            }
        }

        const string CustomPrefix = "custom_level_";
        return levelId.StartsWith(CustomPrefix, StringComparison.OrdinalIgnoreCase)
            ? levelId.Substring(CustomPrefix.Length).ToLowerInvariant()
            : levelId.Trim().ToLowerInvariant();
    }

    private static IReadOnlyList<string> ReadGameplayModifiers(object? gameplayModifiers)
    {
        if (gameplayModifiers == null)
        {
            return Array.Empty<string>();
        }

        var results = new List<string>();
        TryAppendModifier(gameplayModifiers, results, "noFailOn0Energy", "_noFailOn0Energy", "NF");
        TryAppendModifier(gameplayModifiers, results, "instaFail", "_instaFail", "IF");
        TryAppendModifier(gameplayModifiers, results, "noBombs", "_noBombs", "NB");
        TryAppendModifier(gameplayModifiers, results, "noArrows", "_noArrows", "NA");
        TryAppendModifier(gameplayModifiers, results, "disappearingArrows", "_disappearingArrows", "DA");
        TryAppendSongSpeedModifier(gameplayModifiers, results);
        TryAppendModifier(gameplayModifiers, results, "ghostNotes", "_ghostNotes", "GN");
        TryAppendModifier(gameplayModifiers, results, "smallCubes", "_smallCubes", "SC");
        TryAppendModifier(gameplayModifiers, results, "proMode", "_proMode", "PM");
        TryAppendModifier(gameplayModifiers, results, "strictAngles", "_strictAngles", "SA");
        TryAppendNoObstaclesModifier(gameplayModifiers, results);
        TryAppendBatteryEnergyModifier(gameplayModifiers, results);
        TryAppendModifier(gameplayModifiers, results, "zenMode", "_zenMode", "ZM");
        TryAppendModifier(gameplayModifiers, results, "noTextsAndHuds", "_noTextsAndHuds", "NTH");
        return results;
    }

    private static bool IsNoTextsAndHudsSettingEnabled(object? value)
    {
        return ReadBool(
            value,
            "noTextsAndHuds",
            "_noTextsAndHuds",
            "noTextsAndHUDs",
            "_noTextsAndHUDs",
            "hideTextsAndHuds",
            "_hideTextsAndHuds",
            "hideTextsAndHUDs",
            "_hideTextsAndHUDs",
            "hideTextAndHuds",
            "_hideTextAndHuds",
            "hideTextAndHUDs",
            "_hideTextAndHUDs") == true;
    }

    private static void TryAppendNoObstaclesModifier(object target, ICollection<string> output)
    {
        var value = ReadMember(target, "enabledObstacleType", "_enabledObstacleType");
        var raw = value?.ToString() ?? string.Empty;
        if (raw.IndexOf("NoObstacles", StringComparison.OrdinalIgnoreCase) >= 0
            || raw.IndexOf("No Obstacles", StringComparison.OrdinalIgnoreCase) >= 0
            || raw == "2")
        {
            output.Add("NO");
        }
    }

    private static void TryAppendBatteryEnergyModifier(object target, ICollection<string> output)
    {
        var value = ReadMember(target, "energyType", "_energyType");
        var raw = value?.ToString() ?? string.Empty;
        if (raw.IndexOf("Battery", StringComparison.OrdinalIgnoreCase) >= 0
            || raw == "1")
        {
            output.Add("BE");
        }
    }

    private static void TryAppendSongSpeedModifier(object target, ICollection<string> output)
    {
        var value = ReadMember(target, "songSpeed", "_songSpeed");
        if (value == null)
        {
            return;
        }

        var raw = value.ToString() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return;
        }

        if (raw.IndexOf("Slower", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            output.Add("SS");
            return;
        }

        if (raw.IndexOf("SuperFast", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            output.Add("SF");
            return;
        }

        if (raw.IndexOf("Faster", StringComparison.OrdinalIgnoreCase) >= 0 || raw.IndexOf("Fast", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            output.Add("FS");
            return;
        }

        if (!int.TryParse(raw, out var numeric) || numeric <= 0)
        {
            return;
        }

        // Beat Saber enum values differ across versions; map conservatively to BL codes.
        var mapped = numeric switch
        {
            1 => "FS",
            2 => "SS",
            3 => "SF",
            _ => null
        };

        if (!string.IsNullOrWhiteSpace(mapped))
        {
            output.Add(mapped!);
        }
    }

    private static void TryAppendModifier(object target, ICollection<string> output, string propertyName, string fieldName, params string[] labels)
    {
        var value = ReadMember(target, propertyName, fieldName);
        if (value == null)
        {
            return;
        }

        if (value is bool flag)
        {
            if (flag && labels.Length > 0)
            {
                output.Add(labels[0]);
            }

            return;
        }

        var raw = value.ToString();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return;
        }

        if (!int.TryParse(raw, out var numeric))
        {
            return;
        }

        if (numeric <= 0)
        {
            return;
        }

        var index = Math.Min(Math.Max(numeric - 1, 0), Math.Max(0, labels.Length - 1));
        if (labels.Length > 0)
        {
            output.Add(labels[index]);
        }
    }

    private sealed class BeatSaberSessionMetadata
    {
        public string SetupType { get; set; } = string.Empty;
        public string Hash { get; set; } = string.Empty;
        public string Difficulty { get; set; } = string.Empty;
        public string Mode { get; set; } = string.Empty;
        public string SongTitle { get; set; } = string.Empty;
        public string SongAuthor { get; set; } = string.Empty;
        public string Mapper { get; set; } = string.Empty;
        public string LevelId { get; set; } = string.Empty;
        public IReadOnlyList<string> ActiveModifiers { get; set; } = Array.Empty<string>();
        public IReadOnlyList<double> NoteTimes { get; set; } = Array.Empty<double>();
        public object? RuntimeBeatmapData { get; set; }
        public double? SongLengthSeconds { get; set; }
        public double? BeatsPerMinute { get; set; }
        public bool IsReplayMode { get; set; }
        public bool IsPracticeMode { get; set; }
        public bool HudSuppressedByGame { get; set; }
        public long? ReplayScoreId { get; set; }

        public string ToLogMessage()
        {
            var values = new Dictionary<string, string>
            {
                ["setupType"] = SetupType,
                ["hash"] = Hash,
                ["difficulty"] = Difficulty,
                ["mode"] = Mode,
                ["title"] = SongTitle,
                ["songAuthor"] = SongAuthor,
                ["mapper"] = Mapper,
                ["levelId"] = LevelId,
                ["mods"] = ActiveModifiers.Count == 0 ? "(none)" : string.Join(",", ActiveModifiers),
                ["noteTimes"] = NoteTimes.Count.ToString(),
                ["bpm"] = BeatsPerMinute?.ToString("0.###") ?? string.Empty,
                ["isReplay"] = IsReplayMode.ToString(),
                ["isPractice"] = IsPracticeMode.ToString(),
                ["hudSuppressed"] = HudSuppressedByGame.ToString(),
                ["replayScoreId"] = ReplayScoreId?.ToString() ?? string.Empty
            };

            return string.Join("; ", values.Select(pair => $"{pair.Key}={ValueOrUnknown(pair.Value)}"));
        }

        public Sessions.BeatmapSessionInfo ToBeatmapSessionInfo()
        {
            return new Sessions.BeatmapSessionInfo
            {
                Hash = Hash,
                Difficulty = Difficulty,
                Mode = string.IsNullOrWhiteSpace(Mode) ? "Standard" : Mode,
                SongName = SongTitle,
                MapperName = Mapper,
                ActiveModifiers = ActiveModifiers,
                NoteTimes = NoteTimes,
                RuntimeBeatmapData = RuntimeBeatmapData,
                SongLengthSeconds = SongLengthSeconds,
                BeatsPerMinute = BeatsPerMinute,
                IsReplayMode = IsReplayMode,
                IsPracticeMode = IsPracticeMode,
                HudSuppressedByGame = HudSuppressedByGame,
                ReplayScoreId = ReplayScoreId
            };
        }

        private static string ValueOrUnknown(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? "(unknown)" : value;
        }
    }
}
#endif
