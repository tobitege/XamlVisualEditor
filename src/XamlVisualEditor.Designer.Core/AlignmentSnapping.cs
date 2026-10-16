using System;
using System.Collections.Generic;

namespace XamlVisualEditor.Designer.Core;

/// <summary>Finds alignment offsets independently of the rendering backend.</summary>
public static class AlignmentSnapping
{
    /// <summary>Returns the nearest line within the tolerance, including exact matches; no match has a null line.</summary>
    public static (double Offset, double? Line) FindSnapOffset(
        IReadOnlyList<double> primaryLines, IReadOnlyList<double> candidateLines, double threshold)
    {
        double bestDelta = 0;
        double? bestLine = null;
        double bestDistance = double.PositiveInfinity;
        for (int i = 0; i < primaryLines.Count; i++)
        {
            for (int j = 0; j < candidateLines.Count; j++)
            {
                double delta = candidateLines[j] - primaryLines[i];
                double distance = Math.Abs(delta);
                if (distance <= threshold && distance < bestDistance)
                {
                    bestDistance = distance;
                    bestDelta = delta;
                    bestLine = candidateLines[j];
                }
            }
        }
        return (bestDelta, bestLine);
    }
}
