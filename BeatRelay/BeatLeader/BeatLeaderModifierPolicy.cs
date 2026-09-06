using System;
using System.Collections.Generic;
using System.Linq;

namespace BeatRelay.BeatLeader;

public static class BeatLeaderModifierPolicy
{
    private static readonly HashSet<string> ScoreAffectingModifiers = new(StringComparer.OrdinalIgnoreCase)
    {
        "SS",
        "FS",
        "SF",
        "NB",
        "NO",
        "NA"
    };

    public static bool IsZenMode(IReadOnlyList<string>? modifiers)
    {
        return ContainsModifier(modifiers, "ZM");
    }

    public static bool HasNoFail(IReadOnlyList<string>? modifiers)
    {
        return ContainsModifier(modifiers, "NF");
    }

    public static bool ShouldApplyNoFailPenalty(IReadOnlyList<string>? modifiers, bool levelFailed)
    {
        return levelFailed && HasNoFail(modifiers);
    }

    public static IReadOnlyList<string> GetScoringModifiers(IReadOnlyList<string>? modifiers, bool includeNoFailPenalty)
    {
        if (modifiers == null || modifiers.Count == 0)
        {
            return Array.Empty<string>();
        }

        var normalized = modifiers
            .Select(NormalizeModifierToken)
            .Where(modifier => !string.IsNullOrWhiteSpace(modifier))
            .Where(modifier => ScoreAffectingModifiers.Contains(modifier!) || (includeNoFailPenalty && string.Equals(modifier, "NF", StringComparison.OrdinalIgnoreCase)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return normalized.Count == 0 ? Array.Empty<string>() : normalized!;
    }

    public static IReadOnlyList<string> GetDisplayModifiers(IReadOnlyList<string>? modifiers, bool includeNoFail)
    {
        if (modifiers == null || modifiers.Count == 0)
        {
            return Array.Empty<string>();
        }

        var normalized = modifiers
            .Select(NormalizeModifierToken)
            .Where(modifier => !string.IsNullOrWhiteSpace(modifier))
            .Where(modifier => !string.Equals(modifier, "ZM", StringComparison.OrdinalIgnoreCase))
            .Where(modifier => includeNoFail || !string.Equals(modifier, "NF", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return normalized.Count == 0 ? Array.Empty<string>() : normalized!;
    }

    public static string NormalizeModifierToken(string? modifier)
    {
        if (modifier == null || string.IsNullOrWhiteSpace(modifier))
        {
            return string.Empty;
        }

        var token = modifier.Trim().ToUpperInvariant().Replace(" ", string.Empty).Replace("-", string.Empty).Replace("_", string.Empty);
        return token switch
        {
            "SLOWER" or "SLOWERSONG" => "SS",
            "FASTER" or "FASTERSONG" => "FS",
            "SUPERFAST" or "SUPERFASTSONG" or "SFS" => "SF",
            "NOBOMBS" => "NB",
            "NOWALLS" or "NOOBSTACLES" => "NO",
            "NOARROWS" or "NONOTES" => "NA",
            "NOFAIL" or "NOFAILON0ENERGY" => "NF",
            "ZEN" or "ZENMODE" => "ZM",
            _ => token
        };
    }

    private static bool ContainsModifier(IReadOnlyList<string>? modifiers, string expected)
    {
        return modifiers != null
            && modifiers.Any(modifier => string.Equals(NormalizeModifierToken(modifier), expected, StringComparison.OrdinalIgnoreCase));
    }
}
