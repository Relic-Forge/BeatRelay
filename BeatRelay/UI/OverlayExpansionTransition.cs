using System;

namespace BeatRelay.UI;

public sealed class OverlayExpansionTransition
{
    public const double DurationSeconds = 0.24d;
    private double start;
    private double target;
    private double elapsed;
    public double Value { get; private set; }

    public void Reset(double value)
    {
        Value = start = target = Clamp01(value);
        elapsed = DurationSeconds;
    }

    public double Advance(double nextTarget, double unscaledDeltaTime)
    {
        nextTarget = Clamp01(nextTarget);
        if (nextTarget != target)
        {
            start = Value;
            target = nextTarget;
            elapsed = 0d;
        }

        if (double.IsNaN(unscaledDeltaTime) || unscaledDeltaTime <= 0d || Value == target)
        {
            return Value;
        }

        elapsed = Math.Min(DurationSeconds, elapsed + unscaledDeltaTime);
        Value = elapsed >= DurationSeconds ? target : start + ((target - start) * Ease(elapsed / DurationSeconds));
        return Value;
    }

    public static double Ease(double progress)
    {
        var clamped = Clamp01(progress);
        return (1d - Math.Exp(-5d * clamped)) / (1d - Math.Exp(-5d));
    }

    public static bool IsInProgress(double current, double target)
    {
        return Clamp01(current) != Clamp01(target);
    }

    private static double Clamp01(double value)
    {
        if (double.IsNaN(value) || value <= 0d)
        {
            return 0d;
        }

        if (value >= 1d || double.IsPositiveInfinity(value))
        {
            return 1d;
        }

        return value;
    }
}
