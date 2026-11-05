using System;
using System.Collections.Generic;
using UnityEngine;

namespace GokouKotori.FBXUVTextureTransfer
{
    // Moves the aligned source so each landmark centre lands on centre + move, falling off smoothly to nothing at
    // the radius. The same compactly supported RBF as the correction pins, with the same fold-over bound.
    internal static class FBXUVBodyLandmarkWarp
    {
        internal const float RadiusMeters = 0.12f;
        internal const string FlipReason = "ずらすと絵が裏返るため、使いませんでした";

        internal static bool TryWarp(Vector3[] aligned, IReadOnlyList<Vector3> centers, IReadOnlyList<Vector3> moves, float radius,
            out Vector3[] warped, out string reason)
        {
            if (aligned == null) throw new ArgumentNullException(nameof(aligned));
            if (centers == null) throw new ArgumentNullException(nameof(centers));
            if (moves == null) throw new ArgumentNullException(nameof(moves));
            warped = null;
            reason = FlipReason;
            var count = centers.Count;
            if (count != moves.Count || !(radius > 0f)) return false;

            var system = new double[count, count + 3];
            for (var i = 0; i < count; i++)
            {
                for (var j = 0; j < count; j++) system[i, j] = FBXUVBodyPinSolver.Kernel((centers[i] - centers[j]).magnitude / (double)radius);
                system[i, count] = moves[i].x;
                system[i, count + 1] = moves[i].y;
                system[i, count + 2] = moves[i].z;
            }
            if (!FBXUVBodyPinSolver.Solve(system, count)) return false;

            var weights = new Vector3[count];
            var bound = 0.0;
            for (var k = 0; k < count; k++)
            {
                double x = system[k, count], y = system[k, count + 1], z = system[k, count + 2];
                weights[k] = new Vector3((float)x, (float)y, (float)z);
                bound += Math.Sqrt(x * x + y * y + z * z) * FBXUVBodyPinSolver.KernelSlope / radius;
            }
            if (!(bound < FBXUVBodyPinWarp.LipschitzLimit)) return false;

            var result = new Vector3[aligned.Length];
            for (var v = 0; v < result.Length; v++)
            {
                var p = aligned[v];
                var offset = Vector3.zero;
                for (var k = 0; k < count; k++)
                {
                    var t = (p - centers[k]).magnitude / (double)radius;
                    if (t >= 1.0) continue;
                    offset += weights[k] * (float)FBXUVBodyPinSolver.Kernel(t);
                }
                result[v] = p + offset;
            }
            warped = result;
            reason = null;
            return true;
        }
    }
}
