using System;
using System.Collections.Generic;
using UnityEngine;

namespace GokouKotori.FBXUVTextureTransfer
{
    [Serializable]
    public sealed class FBXUVMakeupLandmark
    {
        public string name = string.Empty;
        public Vector2 sourceUv;
        public Vector2 targetUv;
    }

    /// <summary>Inverse thin plate spline: target UV to source UV, with a degree-one polynomial.</summary>
    public sealed class FBXUVMakeupWarp
    {
        public const int MaxLandmarks = 128;
        private readonly Vector2[] targets;
        private readonly double[,] coefficients;
        private readonly double regularization;
        public int Count => targets.Length;

        private FBXUVMakeupWarp(Vector2[] targets, double[,] coefficients, double regularization)
        {
            this.targets = targets;
            this.coefficients = coefficients;
            this.regularization = regularization;
        }

        public static bool TryCreate(IReadOnlyList<FBXUVMakeupLandmark> landmarks,
            out FBXUVMakeupWarp warp, out string reason)
        {
            return TryCreate(landmarks, 0f, out warp, out reason);
        }

        public static bool TryCreate(IReadOnlyList<FBXUVMakeupLandmark> landmarks, float smoothing,
            out FBXUVMakeupWarp warp, out string reason)
        {
            warp = null;
            if (float.IsNaN(smoothing) || float.IsInfinity(smoothing) || smoothing < 0f || smoothing > .1f)
                return Fail("変形の安定化は0～0.1の有限値にしてください。", out reason);
            if (landmarks == null || landmarks.Count < 3 || landmarks.Count > MaxLandmarks)
                return Fail("対応点は3～128点必要です。", out reason);
            var count = landmarks.Count;
            var source = new Vector2[count];
            var target = new Vector2[count];
            for (var i = 0; i < count; i++)
            {
                var point = landmarks[i];
                if (point == null || !IsUnitUv(point.sourceUv) || !IsUnitUv(point.targetUv))
                    return Fail("対応点のUVは有限な0～1の値にしてください。", out reason);
                source[i] = point.sourceUv;
                target[i] = point.targetUv;
            }
            if (!ValidateDomain(source) || !ValidateDomain(target))
                return Fail("転送元・転送先それぞれに、重複せず一直線上にない対応点が必要です。", out reason);

            var size = count + 3;
            var minimum = target[0];
            var maximum = target[0];
            for (var i = 1; i < count; i++)
            {
                minimum = Vector2.Min(minimum, target[i]);
                maximum = Vector2.Max(maximum, target[i]);
            }
            var span = Math.Max((double)maximum.x - minimum.x, (double)maximum.y - minimum.y);
            var regularization = smoothing * span * span;
            var matrix = new double[size, size + 2];
            for (var i = 0; i < count; i++)
            {
                for (var j = 0; j < count; j++)
                    matrix[i, j] = Kernel(target[i].x - (double)target[j].x, target[i].y - (double)target[j].y);
                matrix[i, i] += regularization;
                matrix[i, count] = matrix[count, i] = 1.0;
                matrix[i, count + 1] = matrix[count + 1, i] = target[i].x;
                matrix[i, count + 2] = matrix[count + 2, i] = target[i].y;
                matrix[i, size] = source[i].x;
                matrix[i, size + 1] = source[i].y;
            }
            // Partial pivoting in double precision. At zero smoothing registered points interpolate exactly.
            for (var column = 0; column < size; column++)
            {
                var pivot = column;
                for (var row = column + 1; row < size; row++)
                    if (Math.Abs(matrix[row, column]) > Math.Abs(matrix[pivot, column])) pivot = row;
                if (Math.Abs(matrix[pivot, column]) < 1e-12)
                    return Fail("対応点から安定した変形を計算できません。近接した点や配置を調整してください。", out reason);
                if (pivot != column)
                    for (var j = column; j < size + 2; j++)
                    {
                        var temporary = matrix[column, j];
                        matrix[column, j] = matrix[pivot, j];
                        matrix[pivot, j] = temporary;
                    }
                for (var row = column + 1; row < size; row++)
                {
                    var factor = matrix[row, column] / matrix[column, column];
                    matrix[row, column] = 0.0;
                    for (var j = column + 1; j < size + 2; j++) matrix[row, j] -= factor * matrix[column, j];
                }
            }
            var solution = new double[size, 2];
            for (var row = size - 1; row >= 0; row--)
                for (var axis = 0; axis < 2; axis++)
                {
                    var value = matrix[row, size + axis];
                    for (var j = row + 1; j < size; j++) value -= matrix[row, j] * solution[j, axis];
                    solution[row, axis] = value / matrix[row, row];
                    if (double.IsNaN(solution[row, axis]) || double.IsInfinity(solution[row, axis])
                        || Math.Abs(solution[row, axis]) > float.MaxValue)
                        return Fail("変形係数が有限範囲を超えています。", out reason);
                }
            warp = new FBXUVMakeupWarp(target, solution, regularization);
            reason = string.Empty;
            return true;
        }

        // At a control K*w + P*a = source - lambda*w. This also preserves
        // the original float position exactly at lambda=0 without changing modes.
        internal Vector2 FittedControl(int index, Vector2 source)
        {
            return new Vector2((float)(source.x - regularization * coefficients[index, 0]),
                (float)(source.y - regularization * coefficients[index, 1]));
        }

        public Vector2 Evaluate(Vector2 targetUv)
        {
            var x = coefficients[Count, 0] + targetUv.x * coefficients[Count + 1, 0] + targetUv.y * coefficients[Count + 2, 0];
            var y = coefficients[Count, 1] + targetUv.x * coefficients[Count + 1, 1] + targetUv.y * coefficients[Count + 2, 1];
            for (var i = 0; i < Count; i++)
            {
                var basis = Kernel(targetUv.x - (double)targets[i].x, targetUv.y - (double)targets[i].y);
                x += basis * coefficients[i, 0];
                y += basis * coefficients[i, 1];
            }
            return new Vector2((float)x, (float)y);
        }

        public float GetMaximumControlError(IReadOnlyList<FBXUVMakeupLandmark> landmarks)
        {
            var maximum = 0f;
            if (landmarks == null) return maximum;
            foreach (var point in landmarks)
                if (point != null) maximum = Mathf.Max(maximum, Vector2.Distance(Evaluate(point.targetUv), point.sourceUv));
            return maximum;
        }

        /// <summary>Analytic Jacobian determinant of the target-to-source map.</summary>
        public double EvaluateDeterminant(Vector2 targetUv)
        {
            var ux = coefficients[Count + 1, 0];
            var uy = coefficients[Count + 1, 1];
            var vx = coefficients[Count + 2, 0];
            var vy = coefficients[Count + 2, 1];
            for (var i = 0; i < Count; i++)
            {
                var dx = targetUv.x - (double)targets[i].x;
                var dy = targetUv.y - (double)targets[i].y;
                var squared = dx * dx + dy * dy;
                if (squared == 0) continue;
                var factor = Math.Log(squared) + 1;
                ux += dx * factor * coefficients[i, 0];
                uy += dx * factor * coefficients[i, 1];
                vx += dy * factor * coefficients[i, 0];
                vy += dy * factor * coefficients[i, 1];
            }
            return ux * vy - vx * uy;
        }

        public FBXUVMakeupWarpInspection InspectRegion(IReadOnlyList<FBXUVTriangle> triangles)
        {
            var result = new FBXUVMakeupWarpInspection();
            if (triangles == null || triangles.Count == 0) return result;
            // Deterministic, bounded probes of the selected face, including regions
            // where the current PNG may be transparent. This is not a proof of injectivity.
            var count = Math.Min(1024, triangles.Count);
            for (var sample = 0; sample < count; sample++)
            {
                var triangle = triangles[(int)((long)sample * triangles.Count / count)];
                if (triangle == null) continue;
                result.Add(EvaluateDeterminant((triangle.a + triangle.b + triangle.c) / 3f));
                result.Add(EvaluateDeterminant(triangle.a * .6f + triangle.b * .2f + triangle.c * .2f));
                result.Add(EvaluateDeterminant(triangle.a * .2f + triangle.b * .6f + triangle.c * .2f));
                result.Add(EvaluateDeterminant(triangle.a * .2f + triangle.b * .2f + triangle.c * .6f));
            }
            return result;
        }

        internal Vector4[] GetGpuPoints()
        {
            var points = new Vector4[MaxLandmarks];
            for (var i = 0; i < Count; i++) points[i] = new Vector4(targets[i].x, targets[i].y, (float)coefficients[i, 0], (float)coefficients[i, 1]);
            return points;
        }

        internal Vector4 GetGpuAffine(int index)
        {
            return new Vector4((float)coefficients[Count + index, 0], (float)coefficients[Count + index, 1], 0f, 0f);
        }

        internal static bool IsUnitUv(Vector2 uv)
        {
            return !float.IsNaN(uv.x) && !float.IsNaN(uv.y) && uv.x >= 0f && uv.x <= 1f && uv.y >= 0f && uv.y <= 1f;
        }

        private static bool ValidateDomain(Vector2[] points)
        {
            for (var i = 0; i < points.Length; i++)
                for (var j = 0; j < i; j++)
                    if ((points[i] - points[j]).sqrMagnitude <= 1e-12f) return false;
            var delta = points[1] - points[0];
            for (var i = 2; i < points.Length; i++)
            {
                var other = points[i] - points[0];
                if (Math.Abs(delta.x * (double)other.y - delta.y * (double)other.x) > 1e-10) return true;
            }
            return false;
        }

        private static double Kernel(double x, double y)
        {
            var squared = x * x + y * y;
            return squared > 0.0 ? 0.5 * squared * Math.Log(squared) : 0.0;
        }

        private static bool Fail(string message, out string reason) { reason = message; return false; }
    }

    public sealed class FBXUVMakeupWarpInspection
    {
        public int SampleCount { get; private set; }
        public int PositiveCount { get; private set; }
        public int NegativeCount { get; private set; }
        public int SingularCount { get; private set; }
        public double MinimumDeterminant { get; private set; } = double.PositiveInfinity;
        public double MaximumDeterminant { get; private set; } = double.NegativeInfinity;
        public bool HasFold => PositiveCount > 0 && NegativeCount > 0;
        public bool IsMirrored => PositiveCount == 0 && NegativeCount > 0;

        internal void Add(double value)
        {
            SampleCount++;
            if (double.IsNaN(value) || double.IsInfinity(value) || Math.Abs(value) <= 1e-8) SingularCount++;
            else if (value > 0) PositiveCount++;
            else NegativeCount++;
            if (!double.IsNaN(value))
            {
                MinimumDeterminant = Math.Min(MinimumDeterminant, value);
                MaximumDeterminant = Math.Max(MaximumDeterminant, value);
            }
        }
    }
}
