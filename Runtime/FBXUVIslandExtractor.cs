using System;
using System.Collections.Generic;
using UnityEngine;

namespace GokouKotori.FBXUVTextureTransfer
{
    [Serializable]
    public sealed class FBXUVIsland
    {
        public int id;
        public List<FBXUVTriangle> triangles = new List<FBXUVTriangle>();
        public FBXUVBounds bounds;

        public int Id { get { return id; } }
        public List<FBXUVTriangle> Triangles { get { return triangles; } }
        public FBXUVBounds Bounds { get { return bounds; } }

        public FBXUVIsland(int id, List<FBXUVTriangle> triangles)
        {
            this.id = id;
            this.triangles = triangles ?? new List<FBXUVTriangle>();
            bounds = FBXUVBounds.FromTriangles(this.triangles);
        }
    }

    public static class FBXUVIslandExtractor
    {
        public const float EdgeQuantizationEpsilon = 0.000001f;
        private const double RelativeDegeneracyEpsilon = 1e-12;
        private const double BarycentricEpsilon = 1e-6;

        public static List<FBXUVIsland> Extract(Mesh mesh, int subMeshIndex, int uvChannel)
        {
            return Extract(FBXUVMeshUtility.ExtractTriangles(mesh, subMeshIndex, uvChannel));
        }

        public static List<FBXUVIsland> Extract(IReadOnlyList<FBXUVTriangle> triangles)
        {
            if (triangles == null) throw new ArgumentNullException(nameof(triangles));
            var adjacency = BuildAdjacency(triangles);
            var visited = new bool[triangles.Count];
            var result = new List<FBXUVIsland>();
            for (var first = 0; first < triangles.Count; first++)
            {
                if (visited[first]) continue;
                var connected = new List<FBXUVTriangle>();
                var queue = new Queue<int>();
                visited[first] = true;
                queue.Enqueue(first);
                while (queue.Count > 0)
                {
                    var current = queue.Dequeue();
                    var triangle = triangles[current];
                    if (triangle != null) connected.Add(triangle);
                    var neighbors = adjacency[current];
                    for (var index = 0; index < neighbors.Count; index++)
                    {
                        var neighbor = neighbors[index];
                        if (visited[neighbor]) continue;
                        visited[neighbor] = true;
                        queue.Enqueue(neighbor);
                    }
                }

                result.Add(new FBXUVIsland(result.Count, connected));
            }

            return result;
        }

        public static FBXUVIsland HitTest(IReadOnlyList<FBXUVIsland> islands, Vector2 point)
        {
            if (islands == null) throw new ArgumentNullException(nameof(islands));
            for (var islandIndex = 0; islandIndex < islands.Count; islandIndex++)
            {
                var island = islands[islandIndex];
                if (island == null || !island.bounds.Contains(point)) continue;
                for (var triangleIndex = 0; triangleIndex < island.triangles.Count; triangleIndex++)
                {
                    if (ContainsPoint(island.triangles[triangleIndex], point)) return island;
                }
            }

            return null;
        }

        public static bool ContainsPoint(FBXUVTriangle triangle, Vector2 point)
        {
            if (triangle == null || !IsFinite(triangle.a) || !IsFinite(triangle.b)
                || !IsFinite(triangle.c) || !IsFinite(point)) return false;

            // Cast before subtraction so small UV triangles do not lose precision in float arithmetic.
            var acX = (double)triangle.a.x - triangle.c.x;
            var acY = (double)triangle.a.y - triangle.c.y;
            var bcX = (double)triangle.b.x - triangle.c.x;
            var bcY = (double)triangle.b.y - triangle.c.y;
            var abX = (double)triangle.a.x - triangle.b.x;
            var abY = (double)triangle.a.y - triangle.b.y;
            var denominator = acX * bcY - acY * bcX;
            var maxEdgeLengthSquared = Math.Max(acX * acX + acY * acY,
                Math.Max(bcX * bcX + bcY * bcY, abX * abX + abY * abY));
            if (Math.Abs(denominator) <= maxEdgeLengthSquared * RelativeDegeneracyEpsilon) return false;

            var pcX = (double)point.x - triangle.c.x;
            var pcY = (double)point.y - triangle.c.y;
            var alpha = (pcX * bcY - pcY * bcX) / denominator;
            var beta = (acX * pcY - acY * pcX) / denominator;
            var gamma = 1.0 - alpha - beta;
            return alpha >= -BarycentricEpsilon && beta >= -BarycentricEpsilon && gamma >= -BarycentricEpsilon;
        }

        private static bool IsFinite(Vector2 point)
        {
            return !float.IsNaN(point.x) && !float.IsInfinity(point.x)
                && !float.IsNaN(point.y) && !float.IsInfinity(point.y);
        }

        private static List<int>[] BuildAdjacency(IReadOnlyList<FBXUVTriangle> triangles)
        {
            var edgeOwners = new Dictionary<EdgeKey, List<int>>();
            for (var triangleIndex = 0; triangleIndex < triangles.Count; triangleIndex++)
            {
                var triangle = triangles[triangleIndex];
                if (triangle == null) continue;
                AddOwner(edgeOwners, new EdgeKey(triangle.a, triangle.b), triangleIndex);
                AddOwner(edgeOwners, new EdgeKey(triangle.b, triangle.c), triangleIndex);
                AddOwner(edgeOwners, new EdgeKey(triangle.c, triangle.a), triangleIndex);
            }

            var adjacency = new List<int>[triangles.Count];
            for (var index = 0; index < adjacency.Length; index++) adjacency[index] = new List<int>();
            foreach (var owners in edgeOwners.Values)
            {
                // A star preserves connectivity without creating every pair for overlapping UVs.
                var first = owners[0];
                for (var index = 1; index < owners.Count; index++)
                {
                    var other = owners[index];
                    if (other == first) continue;
                    adjacency[first].Add(other);
                    adjacency[other].Add(first);
                }
            }

            return adjacency;
        }

        private static void AddOwner(Dictionary<EdgeKey, List<int>> owners, EdgeKey key, int triangleIndex)
        {
            List<int> list;
            if (!owners.TryGetValue(key, out list))
            {
                list = new List<int>();
                owners.Add(key, list);
            }
            list.Add(triangleIndex);
        }

        private struct PointKey : IComparable<PointKey>, IEquatable<PointKey>
        {
            public readonly long U;
            public readonly long V;

            public PointKey(Vector2 point)
            {
                U = (long)Math.Round(point.x / EdgeQuantizationEpsilon, MidpointRounding.AwayFromZero);
                V = (long)Math.Round(point.y / EdgeQuantizationEpsilon, MidpointRounding.AwayFromZero);
            }

            public int CompareTo(PointKey other)
            {
                var u = U.CompareTo(other.U);
                return u == 0 ? V.CompareTo(other.V) : u;
            }
            public bool Equals(PointKey other) { return U == other.U && V == other.V; }
            public override bool Equals(object obj) { return obj is PointKey && Equals((PointKey)obj); }
            public override int GetHashCode() { unchecked { return (U.GetHashCode() * 397) ^ V.GetHashCode(); } }
        }

        private struct EdgeKey : IEquatable<EdgeKey>
        {
            public readonly PointKey First;
            public readonly PointKey Second;

            public EdgeKey(Vector2 a, Vector2 b)
            {
                var first = new PointKey(a);
                var second = new PointKey(b);
                if (first.CompareTo(second) <= 0) { First = first; Second = second; }
                else { First = second; Second = first; }
            }

            public bool Equals(EdgeKey other) { return First.Equals(other.First) && Second.Equals(other.Second); }
            public override bool Equals(object obj) { return obj is EdgeKey && Equals((EdgeKey)obj); }
            public override int GetHashCode() { unchecked { return (First.GetHashCode() * 397) ^ Second.GetHashCode(); } }
        }
    }
}
