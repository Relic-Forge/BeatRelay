using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BeatRelay.BeatLeader;

public sealed class LeaderboardScoresResponse
{
    [JsonPropertyName("metadata")]
    public LeaderboardMetadata? Metadata { get; set; }

    [JsonPropertyName("container")]
    public LeaderboardContainer? Container { get; set; }

    [JsonPropertyName("data")]
    public List<BeatLeaderScoreDto> Data { get; set; } = new();

    public IReadOnlyList<BeatLeaderScoreRow> ToScoreRows()
    {
        var rows = new List<BeatLeaderScoreRow>();
        foreach (var score in Data)
        {
            if (score.Rank <= 0 || score.ModifiedScore <= 0)
            {
                continue;
            }

            rows.Add(score.ToScoreRow());
        }

        return rows;
    }
}

public sealed class LeaderboardDetailResponse
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("scores")]
    public List<BeatLeaderScoreDto> Scores { get; set; } = new();
}

public sealed class LeaderboardMetadata
{
    [JsonPropertyName("page")]
    public int Page { get; set; }

    [JsonPropertyName("itemsPerPage")]
    public int ItemsPerPage { get; set; }

    [JsonPropertyName("total")]
    public int Total { get; set; }
}

public sealed class LeaderboardContainer
{
    [JsonPropertyName("leaderboardId")]
    public string? LeaderboardId { get; set; }

    [JsonPropertyName("ranked")]
    public bool Ranked { get; set; }

    [JsonPropertyName("maxScore")]
    public int? MaxScore { get; set; }

    [JsonPropertyName("stars")]
    public double? Stars { get; set; }

    public string SourceName { get; set; } = "BeatLeader";

    public bool? RankByPp { get; set; }

    public bool? SupportsPp { get; set; }

    public bool UsesScoreSaberPpCurve { get; set; }

    public bool PositiveModifiers { get; set; }

    public string? LeaderboardStatus { get; set; }

    public int? RealmId { get; set; }

    public string? RealmName { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtraData { get; set; }

    public double? ResolveStars()
    {
        if (Stars.HasValue)
        {
            return Stars.Value;
        }

        if (ExtraData == null)
        {
            return null;
        }

        if (TryReadDouble(ExtraData, "star", out var star)
            || TryReadDouble(ExtraData, "stars", out star)
            || TryReadDouble(ExtraData, "starRating", out star)
            || TryReadNestedDouble(ExtraData, "difficulty", "stars", out star)
            || TryReadNestedDouble(ExtraData, "leaderboard", "stars", out star))
        {
            return star;
        }

        return null;
    }

    public bool TryResolveScoreModifierMultipliers(
        IReadOnlyList<string> activeModifiers,
        out double positiveMultiplier,
        out double negativeMultiplier)
    {
        positiveMultiplier = 1d;
        negativeMultiplier = 1d;
        if (ExtraData == null || ExtraData.Count == 0 || activeModifiers == null || activeModifiers.Count == 0)
        {
            return false;
        }

        var normalizedMods = activeModifiers
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(NormalizeModifierToken)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (normalizedMods.Count == 0)
        {
            return false;
        }

        foreach (var entry in ExtraData)
        {
            if (string.Equals(entry.Key, "modifierValues", StringComparison.OrdinalIgnoreCase))
            {
                var values = ResolveModifierValues(entry.Value, normalizedMods);
                if (values.HasAnyValue)
                {
                    positiveMultiplier = values.PositiveMultiplier;
                    negativeMultiplier = values.NegativeMultiplier;
                    return true;
                }
            }

            if (TryFindModifierValuesElement(entry.Value, depth: 0, out var modifierValuesElement))
            {
                var values = ResolveModifierValues(modifierValuesElement, normalizedMods);
                if (values.HasAnyValue)
                {
                    positiveMultiplier = values.PositiveMultiplier;
                    negativeMultiplier = values.NegativeMultiplier;
                    return true;
                }
            }
        }

        return false;
    }

    private static bool TryFindModifierValuesElement(JsonElement element, int depth, out JsonElement modifierValuesElement)
    {
        modifierValuesElement = default;
        if (depth > 4)
        {
            return false;
        }

        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, "modifierValues", StringComparison.OrdinalIgnoreCase)
                    && property.Value.ValueKind == JsonValueKind.Object)
                {
                    modifierValuesElement = property.Value;
                    return true;
                }

                if (TryFindModifierValuesElement(property.Value, depth + 1, out modifierValuesElement))
                {
                    return true;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                if (TryFindModifierValuesElement(item, depth + 1, out modifierValuesElement))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static BeatLeaderModifierValues ResolveModifierValues(JsonElement element, IReadOnlyList<string> normalizedMods)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return BeatLeaderModifierValues.Empty;
        }

        var multiplier = 1d;
        var positiveMultiplier = 1d;
        var negativeMultiplier = 1d;
        var used = false;
        foreach (var modifier in normalizedMods)
        {
            if (TryGetDouble(element, modifier.ToLowerInvariant()) is not { } value)
            {
                continue;
            }

            multiplier += value;
            if (value > 0)
            {
                positiveMultiplier += value;
            }
            else if (value < 0)
            {
                negativeMultiplier += value;
            }

            used = true;
        }

        return new BeatLeaderModifierValues(
            used ? multiplier : 1d,
            used ? positiveMultiplier : 1d,
            used ? negativeMultiplier : 1d,
            used,
            false);
    }

    private static string NormalizeModifierToken(string value)
    {
        var token = value.Trim().ToUpperInvariant().Replace(" ", string.Empty).Replace("-", string.Empty).Replace("_", string.Empty);
        return token switch
        {
            "FASTERSONG" => "FS",
            "SLOWERSONG" => "SS",
            "SUPERFASTSONG" => "SF",
            "NOBOMBS" => "NB",
            "NOARROWS" => "NA",
            "NONOTES" => "NA",
            "NOOBSTACLES" => "NO",
            "NOWALLS" => "NO",
            "NOFAILON0ENERGY" => "NF",
            _ => token
        };
    }

    private static bool TryReadDouble(Dictionary<string, JsonElement> values, string key, out double value)
    {
        value = 0;
        if (!values.TryGetValue(key, out var element))
        {
            return false;
        }

        return TryElementDouble(element, out value);
    }

    private static bool TryReadNestedDouble(Dictionary<string, JsonElement> values, string key, string nestedKey, out double value)
    {
        value = 0;
        if (!values.TryGetValue(key, out var parent) || parent.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (!parent.TryGetProperty(nestedKey, out var nested))
        {
            return false;
        }

        return TryElementDouble(nested, out value);
    }

    private static bool TryElementDouble(JsonElement element, out double value)
    {
        value = 0;
        if (element.ValueKind == JsonValueKind.Number)
        {
            return element.TryGetDouble(out value);
        }

        if (element.ValueKind == JsonValueKind.String)
        {
            return double.TryParse(element.GetString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out value);
        }

        return false;
    }

    private static double? TryGetDouble(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number))
        {
            return number;
        }

        if (value.ValueKind == JsonValueKind.String
            && double.TryParse(value.GetString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }

        return null;
    }
}

public sealed class BeatLeaderScoreDto
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("rank")]
    public int Rank { get; set; }

    [JsonPropertyName("baseScore")]
    public int? BaseScore { get; set; }

    [JsonPropertyName("modifiedScore")]
    public int ModifiedScore { get; set; }

    [JsonPropertyName("accuracy")]
    public double? Accuracy { get; set; }

    [JsonPropertyName("pp")]
    public double? Pp { get; set; }

    [JsonPropertyName("modifiers")]
    public string? Modifiers { get; set; }

    [JsonPropertyName("leaderboardId")]
    public string? LeaderboardId { get; set; }

    [JsonPropertyName("player")]
    [JsonConverter(typeof(BeatLeaderPlayerDtoJsonConverter))]
    public BeatLeaderPlayerDto? Player { get; set; }

    public BeatLeaderScoreRow ToScoreRow()
    {
        var playerName = Player?.Name;
        var avatarUrl = string.IsNullOrWhiteSpace(Player?.AvatarUrl) ? Player?.WebAvatarUrl : Player?.AvatarUrl;
        return new BeatLeaderScoreRow
        {
            ScoreId = Id,
            Rank = Rank,
            BaseScore = BaseScore,
            ModifiedScore = ModifiedScore,
            Accuracy = Accuracy,
            Pp = Pp,
            Modifiers = Modifiers ?? string.Empty,
            PlayerId = Player?.Id ?? string.Empty,
            PlayerName = string.IsNullOrWhiteSpace(playerName) ? "Unknown Player" : playerName.Trim(),
            AvatarUrl = NormalizeAvatarUrl(avatarUrl)
        };
    }

    public static string NormalizeAvatarUrl(string? avatarUrl)
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
            return "https://cdn.beatleader.com" + trimmed;
        }

        return trimmed;
    }
}

public sealed class BeatLeaderPlayerDto
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("avatar")]
    public string? AvatarUrl { get; set; }

    [JsonPropertyName("webAvatar")]
    public string? WebAvatarUrl { get; set; }
}

public sealed class BeatLeaderOAuthIdentityDto
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }
}

public sealed class BeatLeaderPlayerDtoJsonConverter : JsonConverter<BeatLeaderPlayerDto>
{
    public override BeatLeaderPlayerDto? Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return null;
        }

        if (reader.TokenType == JsonTokenType.String)
        {
            return new BeatLeaderPlayerDto
            {
                Name = reader.GetString()
            };
        }

        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException($"Unexpected player token: {reader.TokenType}.");
        }

        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;
        return new BeatLeaderPlayerDto
        {
            Id = TryGetString(root, "id"),
            Name = TryGetString(root, "name"),
            AvatarUrl = TryGetString(root, "avatar"),
            WebAvatarUrl = TryGetString(root, "webAvatar")
        };
    }

    public override void Write(
        Utf8JsonWriter writer,
        BeatLeaderPlayerDto value,
        JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("id", value.Id);
        writer.WriteString("name", value.Name);
        writer.WriteString("avatar", value.AvatarUrl);
        writer.WriteString("webAvatar", value.WebAvatarUrl);
        writer.WriteEndObject();
    }

    private static string? TryGetString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.String => property.GetString(),
            JsonValueKind.Number => property.GetRawText(),
            JsonValueKind.True => bool.TrueString,
            JsonValueKind.False => bool.FalseString,
            _ => null
        };
    }
}

public sealed class BeatLeaderSongResponse
{
    [JsonPropertyName("hash")]
    public string? Hash { get; set; }

    [JsonPropertyName("difficulties")]
    public List<BeatLeaderMapDifficultyDto> Difficulties { get; set; } = new();

    [JsonPropertyName("song")]
    public BeatLeaderSongDto? Song { get; set; }

    public IReadOnlyList<BeatLeaderMapDifficultyDto> ResolveDifficulties()
    {
        if (Difficulties.Count > 0)
        {
            return Difficulties;
        }

        return Song == null ? Array.Empty<BeatLeaderMapDifficultyDto>() : Song.Difficulties;
    }
}

public sealed class BeatLeaderSongDto
{
    [JsonPropertyName("hash")]
    public string? Hash { get; set; }

    [JsonPropertyName("difficulties")]
    public List<BeatLeaderMapDifficultyDto> Difficulties { get; set; } = new();
}

public sealed class BeatLeaderMapDifficultyDto
{
    [JsonPropertyName("value")]
    public int? Value { get; set; }

    [JsonPropertyName("difficultyName")]
    public string? DifficultyName { get; set; }

    [JsonPropertyName("modeName")]
    public string? ModeName { get; set; }

    [JsonPropertyName("status")]
    public int? Status { get; set; }

    [JsonPropertyName("maxScore")]
    public int? MaxScore { get; set; }

    [JsonPropertyName("stars")]
    public double? Stars { get; set; }

    [JsonPropertyName("predictedAcc")]
    public double? PredictedAcc { get; set; }

    [JsonPropertyName("passRating")]
    public double? PassRating { get; set; }

    [JsonPropertyName("accRating")]
    public double? AccRating { get; set; }

    [JsonPropertyName("techRating")]
    public double? TechRating { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtraData { get; set; }

    public bool TryResolveModifierRatings(IReadOnlyList<string> activeModifiers, out BeatLeaderModifierRatings ratings)
    {
        ratings = BeatLeaderModifierRatings.Empty;
        if (ExtraData == null || ExtraData.Count == 0 || activeModifiers == null || activeModifiers.Count == 0)
        {
            return false;
        }

        var normalizedMods = activeModifiers
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(NormalizeModifierToken)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (normalizedMods.Count == 0)
        {
            return false;
        }

        var modifierValues = BeatLeaderModifierValues.Empty;
        var modifierValuesIgnoringSpeed = BeatLeaderModifierValues.Empty;
        if (ExtraData.TryGetValue("modifierValues", out var modifierValuesElement))
        {
            modifierValues = ResolveModifierValues(modifierValuesElement, normalizedMods, ignoreSpeedModifiers: false);
            modifierValuesIgnoringSpeed = ResolveModifierValues(modifierValuesElement, normalizedMods, ignoreSpeedModifiers: true);
        }

        foreach (var key in new[] { "modifiersRating", "modifierRatings", "modificationRatings", "modifiers" })
        {
            if (!ExtraData.TryGetValue(key, out var element))
            {
                continue;
            }

            if (TryMatchModifierContainer(element, normalizedMods, modifierValues, modifierValuesIgnoringSpeed, out ratings))
            {
                return ratings.HasAnyValue;
            }
        }

        if (modifierValues.HasAnyValue)
        {
            ratings = new BeatLeaderModifierRatings(null, null, null, null, null, modifierValues.Multiplier, modifierValues.UsedPrecomputedSpeedRatings);
            return true;
        }

        return false;
    }

    public bool TryResolveScoreModifierMultiplier(IReadOnlyList<string> activeModifiers, out double multiplier)
    {
        var hasValues = TryResolveScoreModifierMultipliers(activeModifiers, out var positiveMultiplier, out var negativeMultiplier);
        multiplier = positiveMultiplier * negativeMultiplier;
        return hasValues;
    }

    public bool TryResolveScoreModifierMultipliers(IReadOnlyList<string> activeModifiers, out double positiveMultiplier, out double negativeMultiplier)
    {
        positiveMultiplier = 1d;
        negativeMultiplier = 1d;
        if (ExtraData == null || ExtraData.Count == 0 || activeModifiers == null || activeModifiers.Count == 0)
        {
            return false;
        }

        if (!ExtraData.TryGetValue("modifierValues", out var modifierValuesElement))
        {
            return false;
        }

        var normalizedMods = activeModifiers
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(NormalizeModifierToken)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (normalizedMods.Count == 0)
        {
            return false;
        }

        var values = ResolveModifierValues(modifierValuesElement, normalizedMods, ignoreSpeedModifiers: false);
        positiveMultiplier = values.PositiveMultiplier;
        negativeMultiplier = values.NegativeMultiplier;
        return values.HasAnyValue;
    }

    private static bool TryMatchModifierContainer(JsonElement element, IReadOnlyList<string> normalizedMods, BeatLeaderModifierValues modifierValues, BeatLeaderModifierValues modifierValuesIgnoringSpeed, out BeatLeaderModifierRatings ratings)
    {
        ratings = BeatLeaderModifierRatings.Empty;

        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var modifier in normalizedMods)
            {
                var effectiveModifierValues = IsSpeedModifier(modifier) ? modifierValuesIgnoringSpeed : modifierValues;
                if (TryParseFlatModifierRatings(element, modifier, effectiveModifierValues, out ratings))
                {
                    return ratings.HasAnyValue;
                }
            }

            foreach (var property in element.EnumerateObject())
            {
                if (!ContainsAllTokens(property.Name, normalizedMods))
                {
                    continue;
                }

                if (TryParseRatings(property.Value, out ratings))
                {
                    ratings = ratings.WithModifierValues(ContainsSpeedModifier(normalizedMods) ? modifierValuesIgnoringSpeed : modifierValues);
                    return ratings.HasAnyValue;
                }
            }

            if (TryParseRatings(element, out ratings))
            {
                ratings = ratings.WithModifierValues(ContainsSpeedModifier(normalizedMods) ? modifierValuesIgnoringSpeed : modifierValues);
                return ratings.HasAnyValue;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var label = TryGetString(item, "modifier")
                    ?? TryGetString(item, "mod")
                    ?? TryGetString(item, "modifiers");
                if (!ContainsAllTokens(label ?? string.Empty, normalizedMods))
                {
                    continue;
                }

                if (TryParseRatings(item, out ratings))
                {
                    ratings = ratings.WithModifierValues(ContainsSpeedModifier(normalizedMods) ? modifierValuesIgnoringSpeed : modifierValues);
                    return ratings.HasAnyValue;
                }
            }
        }

        return false;
    }

    private static bool TryParseFlatModifierRatings(JsonElement element, string modifier, BeatLeaderModifierValues modifierValues, out BeatLeaderModifierRatings ratings)
    {
        ratings = BeatLeaderModifierRatings.Empty;
        var prefix = modifier.Trim().ToLowerInvariant();
        var accRating = TryGetDouble(element, prefix + "AccRating");
        var passRating = TryGetDouble(element, prefix + "PassRating");
        var techRating = TryGetDouble(element, prefix + "TechRating");
        if (!accRating.HasValue && !passRating.HasValue && !techRating.HasValue)
        {
            return false;
        }

        ratings = new BeatLeaderModifierRatings(
            null,
            null,
            passRating,
            accRating,
            techRating,
            modifierValues.Multiplier,
            IsSpeedModifier(modifier));
        return true;
    }

    private static BeatLeaderModifierValues ResolveModifierValues(JsonElement element, IReadOnlyList<string> normalizedMods, bool ignoreSpeedModifiers)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return BeatLeaderModifierValues.Empty;
        }

        var multiplier = 1d;
        var positiveMultiplier = 1d;
        var negativeMultiplier = 1d;
        var used = false;
        foreach (var modifier in normalizedMods)
        {
            if (ignoreSpeedModifiers && IsSpeedModifier(modifier))
            {
                continue;
            }

            if (TryGetDouble(element, modifier.ToLowerInvariant()) is not { } value)
            {
                continue;
            }

            multiplier += value;
            if (value > 0)
            {
                positiveMultiplier += value;
            }
            else if (value < 0)
            {
                negativeMultiplier += value;
            }

            used = true;
        }

        return new BeatLeaderModifierValues(
            used ? multiplier : 1d,
            used ? positiveMultiplier : 1d,
            used ? negativeMultiplier : 1d,
            used,
            false);
    }

    private static bool IsSpeedModifier(string modifier)
    {
        return string.Equals(modifier, "FS", StringComparison.OrdinalIgnoreCase)
            || string.Equals(modifier, "SS", StringComparison.OrdinalIgnoreCase)
            || string.Equals(modifier, "SF", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContainsSpeedModifier(IReadOnlyList<string> modifiers)
    {
        return modifiers.Any(IsSpeedModifier);
    }

    private static string NormalizeModifierToken(string value)
    {
        var token = value.Trim().ToUpperInvariant().Replace(" ", string.Empty).Replace("-", string.Empty).Replace("_", string.Empty);
        return token switch
        {
            "FASTERSONG" => "FS",
            "SLOWERSONG" => "SS",
            "SUPERFASTSONG" => "SF",
            "NOBOMBS" => "NB",
            "SMALLCUBES" => "SC",
            "NOARROWS" => "NA",
            "DISAPPEARINGARROWS" => "DA",
            "GHOSTNOTES" => "GN",
            "NOOBSTACLES" => "NO",
            "PROMODE" => "PM",
            "STRICTANGLES" => "SA",
            "BATTERYENERGY" => "BE",
            "NOFAILON0ENERGY" => "NF",
            "INSTAFAIL" => "IF",
            "NOTEXTSANDHUDS" => "NTH",
            "NOTEXTSANDHUD" => "NTH",
            _ => token
        };
    }

    private static bool TryParseRatings(JsonElement element, out BeatLeaderModifierRatings ratings)
    {
        ratings = BeatLeaderModifierRatings.Empty;
        if (element.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var stars = TryGetDouble(element, "stars") ?? TryGetDouble(element, "star") ?? TryGetDouble(element, "starRating");
        var predictedAcc = TryGetDouble(element, "predictedAcc");
        var passRating = TryGetDouble(element, "passRating");
        var accRating = TryGetDouble(element, "accRating");
        var techRating = TryGetDouble(element, "techRating");
        ratings = new BeatLeaderModifierRatings(stars, predictedAcc, passRating, accRating, techRating);
        return ratings.HasAnyValue;
    }

    private static bool ContainsAllTokens(string value, IReadOnlyList<string> tokens)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var normalized = value.ToUpperInvariant();
        foreach (var token in tokens)
        {
            if (normalized.IndexOf(token, StringComparison.Ordinal) < 0)
            {
                return false;
            }
        }

        return true;
    }

    private static string? TryGetString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var text = value.GetString();
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }

    private static double? TryGetDouble(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number))
        {
            return number;
        }

        if (value.ValueKind == JsonValueKind.String
            && double.TryParse(value.GetString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }

        return null;
    }
}

public struct BeatLeaderModifierRatings
{
    public BeatLeaderModifierRatings(double? stars, double? predictedAcc, double? passRating, double? accRating, double? techRating)
        : this(stars, predictedAcc, passRating, accRating, techRating, 1d, false)
    {
    }

    public BeatLeaderModifierRatings(double? stars, double? predictedAcc, double? passRating, double? accRating, double? techRating, double multiplier, bool usedPrecomputedSpeedRatings)
    {
        Stars = stars;
        PredictedAcc = predictedAcc;
        PassRating = passRating;
        AccRating = accRating;
        TechRating = techRating;
        Multiplier = multiplier <= 0 ? 0d : multiplier;
        UsedPrecomputedSpeedRatings = usedPrecomputedSpeedRatings;
    }

    public double? Stars { get; }

    public double? PredictedAcc { get; }

    public double? PassRating { get; }

    public double? AccRating { get; }

    public double? TechRating { get; }

    public double Multiplier { get; }

    public bool UsedPrecomputedSpeedRatings { get; }

    public static BeatLeaderModifierRatings Empty => new(null, null, null, null, null, 1d, false);

    public bool HasAnyValue =>
        Stars.HasValue
        || PredictedAcc.HasValue
        || PassRating.HasValue
        || AccRating.HasValue
        || TechRating.HasValue
        || Math.Abs(Multiplier - 1d) > 0.0001d;

    public BeatLeaderModifierRatings WithModifierValues(BeatLeaderModifierValues values)
    {
        return new BeatLeaderModifierRatings(Stars, PredictedAcc, PassRating, AccRating, TechRating, values.Multiplier, values.UsedPrecomputedSpeedRatings);
    }
}

public struct BeatLeaderModifierValues
{
    public BeatLeaderModifierValues(double multiplier, bool hasAnyValue, bool usedPrecomputedSpeedRatings)
        : this(multiplier, multiplier >= 1d ? multiplier : 1d, multiplier < 1d ? multiplier : 1d, hasAnyValue, usedPrecomputedSpeedRatings)
    {
    }

    public BeatLeaderModifierValues(double multiplier, double positiveMultiplier, double negativeMultiplier, bool hasAnyValue, bool usedPrecomputedSpeedRatings)
    {
        Multiplier = multiplier;
        PositiveMultiplier = positiveMultiplier;
        NegativeMultiplier = negativeMultiplier;
        HasAnyValue = hasAnyValue;
        UsedPrecomputedSpeedRatings = usedPrecomputedSpeedRatings;
    }

    public double Multiplier { get; }

    public double PositiveMultiplier { get; }

    public double NegativeMultiplier { get; }

    public bool HasAnyValue { get; }

    public bool UsedPrecomputedSpeedRatings { get; }

    public static BeatLeaderModifierValues Empty => new(1d, 1d, 1d, false, false);
}
