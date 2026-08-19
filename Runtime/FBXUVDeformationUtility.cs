using System;
using System.Collections.Generic;
using Unity.Profiling;
using UnityEngine;

namespace GokouKotori.FBXUVTextureTransfer
{
    public sealed class FBXUVDeformedVertex
    {
        public Vector2 SourceUv { get; private set; }
        public Vector2 TargetPixel { get; private set; }

        public FBXUVDeformedVertex(Vector2 sourceUv, Vector2 targetPixel)
        {
            SourceUv = sourceUv;
            TargetPixel = targetPixel;
        }
    }

    public static class FBXUVDeformationUtility
    {
        public const float RayStepPixels = 0.5f;
        public const float CoverPaddingPixels = 2f;
        public const float GaussianRadiusRatio = 0.35f;
        private const double VertexQuantizationScale = 1000000000d;
        private static readonly ProfilerMarker DeformationMarker =
            new ProfilerMarker("FBXUVTextureTransfer.Deformation");

        public static IReadOnlyDictionary<Vector2, FBXUVDeformedVertex> CreateVertexMap(
            FBXUVTransferRegion sourceRegion,
            FBXUVTransferRegion targetRegion,
            int targetWidth,
            int targetHeight)
        {
            return CreateVertexMap(
                sourceRegion,
                targetRegion,
                targetWidth,
                targetHeight,
                FBXUVTransferOrientation.Preserve);
        }

        public static IReadOnlyDictionary<Vector2, FBXUVDeformedVertex> CreateVertexMap(
            FBXUVTransferRegion sourceRegion,
            FBXUVTransferRegion targetRegion,
            int targetWidth,
            int targetHeight,
            FBXUVTransferOrientation orientation)
        {
            if (sourceRegion == null) throw new ArgumentNullException(nameof(sourceRegion));
            if (targetRegion == null) throw new ArgumentNullException(nameof(targetRegion));
            if (targetWidth <= 0) throw new ArgumentOutOfRangeException(nameof(targetWidth));
            if (targetHeight <= 0) throw new ArgumentOutOfRangeException(nameof(targetHeight));

            var keyedMap = CreateKeyedVertexMap(sourceRegion, targetRegion, targetWidth, targetHeight, orientation);
            var result = new Dictionary<Vector2, FBXUVDeformedVertex>();
            foreach (var pair in keyedMap)
            {
                result[pair.Value.SourceUv] = pair.Value;
            }
            return result;
        }

        internal static Dictionary<VertexKey, FBXUVDeformedVertex> CreateKeyedVertexMap(
            FBXUVTransferRegion sourceRegion,
            FBXUVTransferRegion targetRegion,
            int targetWidth,
            int targetHeight)
        {
            return CreateKeyedVertexMap(
                sourceRegion,
                targetRegion,
                targetWidth,
                targetHeight,
                FBXUVTransferOrientation.Preserve);
        }

        internal static Dictionary<VertexKey, FBXUVDeformedVertex> CreateKeyedVertexMap(
            FBXUVTransferRegion sourceRegion,
            FBXUVTransferRegion targetRegion,
            int targetWidth,
            int targetHeight,
            FBXUVTransferOrientation orientation)
        {
            using (DeformationMarker.Auto())
            {
                return CreateKeyedVertexMapCore(
                    sourceRegion,
                    targetRegion,
                    targetWidth,
                    targetHeight,
                    orientation,
                    true);
            }
        }

        internal static Dictionary<VertexKey, FBXUVDeformedVertex> CreateKeyedVertexMapWithImageEdgeRayLimitForTests(
            FBXUVTransferRegion sourceRegion,
            FBXUVTransferRegion targetRegion,
            int targetWidth,
            int targetHeight,
            FBXUVTransferOrientation orientation)
        {
            return CreateKeyedVertexMapCore(
                sourceRegion,
                targetRegion,
                targetWidth,
                targetHeight,
                orientation,
                false);
        }

        private static Dictionary<VertexKey, FBXUVDeformedVertex> CreateKeyedVertexMapCore(
            FBXUVTransferRegion sourceRegion,
            FBXUVTransferRegion targetRegion,
            int targetWidth,
            int targetHeight,
            FBXUVTransferOrientation orientation,
            bool limitRayToTargetTriangleBounds)
        {
            var sourceTriangles = GetEffectiveTriangles(sourceRegion);
            var targetTriangles = GetEffectiveTriangles(targetRegion);
            var sourceBounds = sourceRegion.bounds.IsValid ? sourceRegion.bounds : FBXUVBounds.FromTriangles(sourceTriangles);
            var targetBounds = targetRegion.bounds.IsValid ? targetRegion.bounds : FBXUVBounds.FromTriangles(targetTriangles);
            var targetTriangleBounds = FBXUVBounds.FromTriangles(targetTriangles);
            if (!sourceBounds.IsValid
                || !targetBounds.IsValid
                || !targetTriangleBounds.IsValid
                || sourceTriangles.Count == 0
                || targetTriangles.Count == 0)
            {
                return new Dictionary<VertexKey, FBXUVDeformedVertex>();
            }

            var allPoints = new Dictionary<VertexKey, Vector2>();
            for (var triangleIndex = 0; triangleIndex < sourceTriangles.Count; triangleIndex++)
            {
                var triangle = sourceTriangles[triangleIndex];
                AddPoint(allPoints, triangle.a);
                AddPoint(allPoints, triangle.b);
                AddPoint(allPoints, triangle.c);
            }

            var boundaryKeys = FindBoundaryVertexKeys(sourceTriangles);
            var initialPositions = new Dictionary<VertexKey, Vector2>(allPoints.Count);
            var targetRect = ToTopLeftPixelRect(targetBounds, targetWidth, targetHeight);
            var targetTriangleRect = ToTopLeftPixelRect(targetTriangleBounds, targetWidth, targetHeight);
            var center = targetRect.center;
            var effectiveOrientation = IsDefinedOrientation(orientation)
                ? orientation
                : FBXUVTransferOrientation.Preserve;
            foreach (var pair in allPoints)
            {
                initialPositions[pair.Key] = MapSourceUvToTargetRect(pair.Value, sourceBounds, targetRect, effectiveOrientation);
            }

            var boundaryDisplacements = new Dictionary<VertexKey, Vector2>(boundaryKeys.Count);
            foreach (var key in boundaryKeys)
            {
                var initial = initialPositions[key];
                var boundary = FindTargetBoundaryPoint(
                    targetTriangles,
                    targetTriangleRect,
                    center,
                    initial,
                    targetWidth,
                    targetHeight,
                    limitRayToTargetTriangleBounds);
                boundaryDisplacements[key] = boundary - initial;
            }

            var falloffRadius = Math.Max(1d, Math.Min(targetRect.width, targetRect.height) * GaussianRadiusRatio);
            var result = new Dictionary<VertexKey, FBXUVDeformedVertex>(allPoints.Count);
            foreach (var pair in allPoints)
            {
                Vector2 displacement;
                if (!boundaryDisplacements.TryGetValue(pair.Key, out displacement))
                {
                    displacement = InterpolateDisplacement(initialPositions[pair.Key], boundaryDisplacements, initialPositions, falloffRadius);
                }
                result[pair.Key] = new FBXUVDeformedVertex(pair.Value, initialPositions[pair.Key] + displacement);
            }
            return result;
        }

        internal static List<FBXUVTriangle> GetEffectiveTriangles(FBXUVTransferRegion region)
        {
            if (region.triangles != null && region.triangles.Count > 0) return DistinctTriangles(region.triangles);
            if (!region.bounds.IsValid) return new List<FBXUVTriangle>();
            var bounds = region.bounds;
            var topLeft = new Vector2(bounds.minU, bounds.maxV);
            var topRight = new Vector2(bounds.maxU, bounds.maxV);
            var bottomLeft = new Vector2(bounds.minU, bounds.minV);
            var bottomRight = new Vector2(bounds.maxU, bounds.minV);
            return new List<FBXUVTriangle>
            {
                new FBXUVTriangle(0, topLeft, bottomLeft, topRight),
                new FBXUVTriangle(1, topRight, bottomLeft, bottomRight),
            };
        }

        public static Vector2 UvToTopLeftPixel(Vector2 uv, int width, int height)
        {
            return new Vector2(uv.x * width, (1f - uv.y) * height);
        }

        public static Vector2 TopLeftPixelToUv(Vector2 pixel, int width, int height)
        {
            return new Vector2(pixel.x / width, 1f - (pixel.y / height));
        }

        private static Rect ToTopLeftPixelRect(FBXUVBounds bounds, int width, int height)
        {
            return new Rect(
                bounds.minU * width,
                (1f - bounds.maxV) * height,
                (bounds.maxU - bounds.minU) * width,
                (bounds.maxV - bounds.minV) * height);
        }

        public static bool IsDefinedOrientation(FBXUVTransferOrientation orientation)
        {
            return orientation == FBXUVTransferOrientation.Preserve
                || orientation == FBXUVTransferOrientation.FlipHorizontal
                || orientation == FBXUVTransferOrientation.FlipVertical
                || orientation == FBXUVTransferOrientation.Rotate180;
        }

        private static Vector2 MapSourceUvToTargetRect(
            Vector2 point,
            FBXUVBounds sourceBounds,
            Rect targetRect,
            FBXUVTransferOrientation orientation)
        {
            var sourceWidth = Math.Max(sourceBounds.maxU - sourceBounds.minU, float.Epsilon);
            var sourceHeight = Math.Max(sourceBounds.maxV - sourceBounds.minV, float.Epsilon);
            var normalizedU = (point.x - sourceBounds.minU) / sourceWidth;
            var normalizedV = (sourceBounds.maxV - point.y) / sourceHeight;
            if (orientation == FBXUVTransferOrientation.FlipHorizontal
                || orientation == FBXUVTransferOrientation.Rotate180)
            {
                normalizedU = 1f - normalizedU;
            }
            if (orientation == FBXUVTransferOrientation.FlipVertical
                || orientation == FBXUVTransferOrientation.Rotate180)
            {
                normalizedV = 1f - normalizedV;
            }
            return new Vector2(targetRect.x + normalizedU * targetRect.width, targetRect.y + normalizedV * targetRect.height);
        }

        private static Vector2 FindTargetBoundaryPoint(
            IReadOnlyList<FBXUVTriangle> targetTriangles,
            Rect targetTriangleRect,
            Vector2 center,
            Vector2 initial,
            int targetWidth,
            int targetHeight,
            bool limitRayToTargetTriangleBounds)
        {
            var direction = initial - center;
            var length = direction.magnitude;
            if (length <= 0.0001f) return initial;
            direction /= length;
            var maxDistance = DistanceToImageEdge(center, direction, targetWidth, targetHeight);
            if (limitRayToTargetTriangleBounds)
            {
                maxDistance = Mathf.Min(
                    maxDistance,
                    MaximumProjectedDistanceToBounds(center, direction, targetTriangleRect));
            }
            var lastInside = initial;
            for (var distance = 0f; distance <= maxDistance; distance += RayStepPixels)
            {
                var point = center + direction * distance;
                if (ContainsPoint(targetTriangles, TopLeftPixelToUv(point, targetWidth, targetHeight))) lastInside = point;
            }

            var initialDistance = Vector2.Distance(center, initial);
            var targetDistance = Vector2.Distance(center, lastInside) + CoverPaddingPixels;
            return targetDistance <= initialDistance ? initial : center + direction * targetDistance;
        }

        private static float MaximumProjectedDistanceToBounds(Vector2 origin, Vector2 direction, Rect bounds)
        {
            var maxDistance = float.NegativeInfinity;
            maxDistance = Mathf.Max(maxDistance, Vector2.Dot(new Vector2(bounds.xMin, bounds.yMin) - origin, direction));
            maxDistance = Mathf.Max(maxDistance, Vector2.Dot(new Vector2(bounds.xMax, bounds.yMin) - origin, direction));
            maxDistance = Mathf.Max(maxDistance, Vector2.Dot(new Vector2(bounds.xMin, bounds.yMax) - origin, direction));
            maxDistance = Mathf.Max(maxDistance, Vector2.Dot(new Vector2(bounds.xMax, bounds.yMax) - origin, direction));
            return Mathf.Max(0f, maxDistance);
        }

        private static float DistanceToImageEdge(Vector2 origin, Vector2 direction, int width, int height)
        {
            var result = float.PositiveInfinity;
            if (Mathf.Abs(direction.x) > 0.0001f)
            {
                var distance = ((direction.x > 0f ? width - 1f : 0f) - origin.x) / direction.x;
                if (distance >= 0f) result = Mathf.Min(result, distance);
            }
            if (Mathf.Abs(direction.y) > 0.0001f)
            {
                var distance = ((direction.y > 0f ? height - 1f : 0f) - origin.y) / direction.y;
                if (distance >= 0f) result = Mathf.Min(result, distance);
            }
            return float.IsInfinity(result) ? 0f : Mathf.Max(0f, result);
        }

        private static Vector2 InterpolateDisplacement(
            Vector2 point,
            IReadOnlyDictionary<VertexKey, Vector2> boundaryDisplacements,
            IReadOnlyDictionary<VertexKey, Vector2> initialPositions,
            double falloffRadius)
        {
            var sum = Vector2.zero;
            var sumWeight = 0d;
            foreach (var pair in boundaryDisplacements)
            {
                var distance = Vector2.Distance(point, initialPositions[pair.Key]);
                var weight = Math.Exp(-(distance * distance) / (2d * falloffRadius * falloffRadius));
                sum += pair.Value * (float)weight;
                sumWeight += weight;
            }
            return sumWeight <= 0d ? Vector2.zero : sum / (float)sumWeight;
        }

        private static HashSet<VertexKey> FindBoundaryVertexKeys(IReadOnlyList<FBXUVTriangle> triangles)
        {
            var counts = new Dictionary<EdgeKey, int>();
            for (var index = 0; index < triangles.Count; index++)
            {
                var triangle = triangles[index];
                CountEdge(counts, triangle.a, triangle.b);
                CountEdge(counts, triangle.b, triangle.c);
                CountEdge(counts, triangle.c, triangle.a);
            }

            var result = new HashSet<VertexKey>();
            foreach (var pair in counts)
            {
                if (pair.Value != 1) continue;
                result.Add(pair.Key.First);
                result.Add(pair.Key.Second);
            }
            return result;
        }

        private static void CountEdge(Dictionary<EdgeKey, int> counts, Vector2 a, Vector2 b)
        {
            var edge = new EdgeKey(new VertexKey(a), new VertexKey(b));
            int count;
            counts[edge] = counts.TryGetValue(edge, out count) ? count + 1 : 1;
        }

        private static void AddPoint(Dictionary<VertexKey, Vector2> points, Vector2 point)
        {
            var key = new VertexKey(point);
            if (!points.ContainsKey(key)) points.Add(key, point);
        }

        private static bool ContainsPoint(IReadOnlyList<FBXUVTriangle> triangles, Vector2 point)
        {
            for (var index = 0; index < triangles.Count; index++)
            {
                if (FBXUVIslandExtractor.ContainsPoint(triangles[index], point)) return true;
            }
            return false;
        }

        private static List<FBXUVTriangle> DistinctTriangles(IReadOnlyList<FBXUVTriangle> triangles)
        {
            var result = new List<FBXUVTriangle>();
            var seen = new HashSet<TriangleKey>();
            for (var index = 0; index < triangles.Count; index++)
            {
                var triangle = triangles[index];
                if (triangle == null) continue;
                var key = new TriangleKey(new VertexKey(triangle.a), new VertexKey(triangle.b), new VertexKey(triangle.c));
                if (seen.Add(key)) result.Add(triangle);
            }
            return result;
        }

        internal struct VertexKey : IComparable<VertexKey>, IEquatable<VertexKey>
        {
            public readonly long U;
            public readonly long V;

            public VertexKey(Vector2 point)
            {
                U = (long)Math.Round(point.x * VertexQuantizationScale, MidpointRounding.AwayFromZero);
                V = (long)Math.Round(point.y * VertexQuantizationScale, MidpointRounding.AwayFromZero);
            }
            public int CompareTo(VertexKey other) { var u = U.CompareTo(other.U); return u == 0 ? V.CompareTo(other.V) : u; }
            public bool Equals(VertexKey other) { return U == other.U && V == other.V; }
            public override bool Equals(object obj) { return obj is VertexKey && Equals((VertexKey)obj); }
            public override int GetHashCode() { unchecked { return (U.GetHashCode() * 397) ^ V.GetHashCode(); } }
        }

        private struct EdgeKey : IEquatable<EdgeKey>
        {
            public readonly VertexKey First;
            public readonly VertexKey Second;
            public EdgeKey(VertexKey a, VertexKey b)
            {
                if (a.CompareTo(b) <= 0) { First = a; Second = b; }
                else { First = b; Second = a; }
            }
            public bool Equals(EdgeKey other) { return First.Equals(other.First) && Second.Equals(other.Second); }
            public override bool Equals(object obj) { return obj is EdgeKey && Equals((EdgeKey)obj); }
            public override int GetHashCode() { unchecked { return (First.GetHashCode() * 397) ^ Second.GetHashCode(); } }
        }

        private struct TriangleKey : IEquatable<TriangleKey>
        {
            private readonly VertexKey first;
            private readonly VertexKey second;
            private readonly VertexKey third;
            public TriangleKey(VertexKey a, VertexKey b, VertexKey c)
            {
                if (a.CompareTo(b) > 0) Swap(ref a, ref b);
                if (b.CompareTo(c) > 0) Swap(ref b, ref c);
                if (a.CompareTo(b) > 0) Swap(ref a, ref b);
                first = a; second = b; third = c;
            }
            public bool Equals(TriangleKey other) { return first.Equals(other.first) && second.Equals(other.second) && third.Equals(other.third); }
            public override bool Equals(object obj) { return obj is TriangleKey && Equals((TriangleKey)obj); }
            public override int GetHashCode() { unchecked { return ((first.GetHashCode() * 397) ^ second.GetHashCode()) * 397 ^ third.GetHashCode(); } }
            private static void Swap(ref VertexKey a, ref VertexKey b) { var temp = a; a = b; b = temp; }
        }
    }
}
