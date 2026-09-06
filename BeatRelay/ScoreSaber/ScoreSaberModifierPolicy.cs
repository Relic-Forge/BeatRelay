using System;
using System.Collections.Generic;
using System.Linq;

namespace BeatRelay.ScoreSaber;

public static class ScoreSaberModifierPolicy
{
    private static readonly Dictionary<string, double> ModifierValues = new(StringComparer.OrdinalIgnoreCase)
    {
        ["NF"] = -0.50d,
        ["SS"] = -0.30d,
        ["NB"] = -0.10d,
        ["NO"] = -0.05d,
        ["NA"] = -0.30d,
        ["DA"] = 0.07d,
        ["FS"] = 0.08d,
        ["SF"] = 0.10d,
        ["GN"] = 0.11d
    };

    public static bool TryResolveScoreModifierMultipliers(
        IReadOnlyList<string>? activeModifiers,
        bool includeNoFailPenalty,
        bool allowPositiveModifiers,
        out double positiveMultiplier,
        out double negativeMultiplier)
    {
        positiveMultiplier = 1d;
        negativeMultiplier = 1d;
        var normalized = GetScoringModifiers(activeModifiers, includeNoFailPenalty, allowPositiveModifiers);
        if (normalized.Count == 0)
        {
            return false;
        }

        foreach (var modifier in normalized)
        {
            if (!ModifierValues.TryGetValue(modifier, out var value))
            {
                continue;
            }

            if (value > 0d)
            {
                positiveMultiplier += value;
            }
            else if (value < 0d)
            {
                negativeMultiplier += value;
            }
        }

        negativeMultiplier = Math.Max(0d, negativeMultiplier);
        return Math.Abs(positiveMultiplier - 1d) > 0.0001d || Math.Abs(negativeMultiplier - 1d) > 0.0001d;
    }

    public static IReadOnlyList<string> GetDisplayModifiers(IReadOnlyList<string>? activeModifiers, bool includeNoFail)
    {
        if (activeModifiers == null || activeModifiers.Count == 0)
        {
            return Array.Empty<string>();
        }

        return activeModifiers
            .Select(NormalizeModifierToken)
            .Where(modifier => !string.IsNullOrWhiteSpace(modifier))
            .Where(modifier => !string.Equals(modifier, "ZM", StringComparison.OrdinalIgnoreCase))
            .Where(modifier => !string.Equals(modifier, "NF", StringComparison.OrdinalIgnoreCase) || includeNoFail)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList()!;
    }

    private static IReadOnlyList<string> GetScoringModifiers(IReadOnlyList<string>? activeModifiers, bool includeNoFailPenalty, bool allowPositiveModifiers)
    {
        if (activeModifiers == null || activeModifiers.Count == 0)
        {
            return Array.Empty<string>();
        }

        return activeModifiers
            .Select(NormalizeModifierToken)
            .Where(modifier => !string.IsNullOrWhiteSpace(modifier))
            .Where(modifier => !string.Equals(modifier, "NF", StringComparison.OrdinalIgnoreCase) || includeNoFailPenalty)
            .Where(modifier => ModifierValues.ContainsKey(modifier!))
            .Where(modifier => allowPositiveModifiers || ModifierValues[modifier!] <= 0d)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList()!;
    }

    private static string? NormalizeModifierToken(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var token = value.Trim().ToUpperInvariant().Replace(" ", string.Empty).Replace("-", string.Empty).Replace("_", string.Empty);
        return token switch
        {
            "NOFAIL" or "NOFAILON0ENERGY" => "NF",
            "SLOWER" or "SLOWERSONG" => "SS",
            "NOBOMBS" => "NB",
            "NOWALLS" or "NOOBSTACLES" => "NO",
            "NOARROWS" or "NONOTES" => "NA",
            "DISAPPEARINGARROWS" => "DA",
            "FASTER" or "FASTERSONG" => "FS",
            "SUPERFAST" or "SUPERFASTSONG" => "SF",
            "GHOSTNOTES" => "GN",
            "PRO" or "PROMODE" => "PM",
            "SMALLNOTES" or "SMALLCUBES" => "SC",
            "STRICTANGLES" => "SA",
            "ZEN" or "ZENMODE" => "ZM",
            _ => token
        };
    }
}
