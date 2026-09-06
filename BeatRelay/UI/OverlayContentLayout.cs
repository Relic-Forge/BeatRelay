using System;
using System.Collections.Generic;

namespace BeatRelay.UI;

// Zero-width columns are disabled and consume neither space nor a gap.
public sealed class OverlayContentLayout
{
    public double[] Positions { get; }
    public double Width { get; }

    public OverlayContentLayout(IReadOnlyList<double> widths, double gap, double padding)
    {
        Positions = new double[widths.Count];
        var cursor = padding;
        var hasColumn = false;
        for (var i = 0; i < widths.Count; i++)
        {
            Positions[i] = double.NaN;
            if (widths[i] <= 0d) continue;
            if (hasColumn) cursor += gap;
            Positions[i] = cursor;
            cursor += widths[i];
            hasColumn = true;
        }

        Width = cursor + padding;
    }

    public static double InterpolatePosition(double collapsed, double expanded, double progress)
    {
        if (double.IsNaN(collapsed)) return double.IsNaN(expanded) ? 0d : expanded;
        if (double.IsNaN(expanded)) return collapsed;
        return collapsed + ((expanded - collapsed) * progress);
    }

    public static double Reveal(double expansion) => Math.Max(0d, Math.Min(1d, (expansion - 0.15d) / 0.85d));

    public static double FieldOpacity(bool collapsed, bool expanded, double expansion)
    {
        if (collapsed && expanded) return 1d;
        if (!collapsed) return expanded && Reveal(expansion) > 0d ? 1d : 0d;
        var t = Math.Max(0d, Math.Min(1d, expansion / 0.45d));
        return 1d - (t * t * (3d - (2d * t)));
    }
}
