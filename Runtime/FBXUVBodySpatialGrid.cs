using System;
using System.Collections.Generic;
using UnityEngine;

namespace GokouKotori.FBXUVTextureTransfer
{
    internal sealed class FBXUVBodySpatialGrid
    {
        private const float MinNormalDot = 0.5f;

        private readonly Dictionary<long, int[]> cells;
        private readonly Vector3[] positions;
        private readonly int[] triangles;
        private readonly Vector3[] faceNormals;
        private readonly float cellSize;

        internal FBXUVBodySpatialGrid(Vector3[] positions, int[] triangles, Vector3[] faceNormals, float cellSize)
        {
            if (positions == null) throw new ArgumentNullException(nameof(positions));
            if (triangles == null) throw new ArgumentNullException(nameof(triangles));
            if (faceNormals == null) throw new ArgumentNullException(nameof(faceNormals));
            if (!(cellSize > 0f)) throw new ArgumentException("格子の大きさが正ではありません。", nameof(cellSize));

            this.positions = positions;
            this.triangles = triangles;
            this.faceNormals = faceNormals;
            this.cellSize = cellSize;

            var temp = new Dictionary<long, List<int>>();
            for (var t = 0; t < triangles.Length / 3; t++)
            {
                Vector3 a = positions[triangles[t * 3]], b = positions[triangles[t * 3 + 1]], c = positions[triangles[t * 3 + 2]];
                var min = Vector3.Min(a, Vector3.Min(b, c)) / cellSize;
                var max = Vector3.Max(a, Vector3.Max(b, c)) / cellSize;
                for (var x = Mathf.FloorToInt(min.x); x <= Mathf.FloorToInt(max.x); x++)
                for (var y = Mathf.FloorToInt(min.y); y <= Mathf.FloorToInt(max.y); y++)
                for (var z = Mathf.FloorToInt(min.z); z <= Mathf.FloorToInt(max.z); z++)
                {
                    var key = Key(x, y, z);
                    if (!temp.TryGetValue(key, out var list)) temp[key] = list = new List<int>();
                    list.Add(t);
                }
            }

            cells = new Dictionary<long, int[]>(temp.Count);
            foreach (var pair in temp) cells.Add(pair.Key, pair.Value.ToArray());
        }

        private static long Key(int x, int y, int z)
        {
            return ((long)(x & 0x1FFFFF) << 42) | ((long)(y & 0x1FFFFF) << 21) | (long)(z & 0x1FFFFF);
        }

        internal bool Nearest(Vector3 point, Vector3 normal, int maxRing, float maxDistance, out int triangle, out Vector3 barycentric, out float distance)
        {
            int cx = Mathf.FloorToInt(point.x / cellSize), cy = Mathf.FloorToInt(point.y / cellSize), cz = Mathf.FloorToInt(point.z / cellSize);
            var best = maxDistance * maxDistance;
            triangle = -1;
            barycentric = Vector3.zero;
            for (var k = 0; k <= maxRing; k++)
            {
                for (var dx = -k; dx <= k; dx++)
                for (var dy = -k; dy <= k; dy++)
                for (var dz = -k; dz <= k; dz++)
                {
                    if (Math.Max(Math.Abs(dx), Math.Max(Math.Abs(dy), Math.Abs(dz))) != k) continue;
                    if (!cells.TryGetValue(Key(cx + dx, cy + dy, cz + dz), out var list)) continue;
                    foreach (var t in list)
                    {
                        if (Vector3.Dot(faceNormals[t], normal) < MinNormalDot) continue;
                        var closest = ClosestPoint(point, positions[triangles[t * 3]], positions[triangles[t * 3 + 1]], positions[triangles[t * 3 + 2]], out var bary);
                        var d2 = (closest - point).sqrMagnitude;
                        if (d2 < best)
                        {
                            best = d2;
                            triangle = t;
                            barycentric = bary;
                        }
                    }
                }
                if (triangle >= 0 && best <= k * cellSize * k * cellSize) break;
            }
            distance = Mathf.Sqrt(best);
            return triangle >= 0;
        }

        internal static Vector3[] FaceNormals(Vector3[] positions, Vector3[] originalPositions, Vector3[] originalNormals, int[] triangles)
        {
            var result = new Vector3[triangles.Length / 3];
            for (var t = 0; t < result.Length; t++)
            {
                int a = triangles[t * 3], b = triangles[t * 3 + 1], c = triangles[t * 3 + 2];
                var original = Vector3.Cross(originalPositions[b] - originalPositions[a], originalPositions[c] - originalPositions[a]);
                var sign = Vector3.Dot(original, originalNormals[a] + originalNormals[b] + originalNormals[c]) >= 0f ? 1f : -1f;
                result[t] = Normalize(Vector3.Cross(positions[b] - positions[a], positions[c] - positions[a])) * sign;
            }
            return result;
        }

        // Vector3.normalized returns zero below 1e-5 in length, which would drop every small triangle.
        internal static Vector3 Normalize(Vector3 vector)
        {
            var squared = vector.sqrMagnitude;
            return squared > 1e-30f ? vector / Mathf.Sqrt(squared) : Vector3.zero;
        }

        private static Vector3 ClosestPoint(Vector3 p, Vector3 a, Vector3 b, Vector3 c, out Vector3 bary)
        {
            Vector3 ab = b - a, ac = c - a, ap = p - a;
            float d1 = Vector3.Dot(ab, ap), d2 = Vector3.Dot(ac, ap);
            if (d1 <= 0f && d2 <= 0f) { bary = new Vector3(1, 0, 0); return a; }
            var bp = p - b;
            float d3 = Vector3.Dot(ab, bp), d4 = Vector3.Dot(ac, bp);
            if (d3 >= 0f && d4 <= d3) { bary = new Vector3(0, 1, 0); return b; }
            var vc = d1 * d4 - d3 * d2;
            if (vc <= 0f && d1 >= 0f && d3 <= 0f) { var t = d1 / (d1 - d3); bary = new Vector3(1 - t, t, 0); return a + t * ab; }
            var cp = p - c;
            float d5 = Vector3.Dot(ab, cp), d6 = Vector3.Dot(ac, cp);
            if (d6 >= 0f && d5 <= d6) { bary = new Vector3(0, 0, 1); return c; }
            var vb = d5 * d2 - d1 * d6;
            if (vb <= 0f && d2 >= 0f && d6 <= 0f) { var t = d2 / (d2 - d6); bary = new Vector3(1 - t, 0, t); return a + t * ac; }
            var va = d3 * d6 - d5 * d4;
            if (va <= 0f && d4 - d3 >= 0f && d5 - d6 >= 0f) { var t = (d4 - d3) / ((d4 - d3) + (d5 - d6)); bary = new Vector3(0, 1 - t, t); return b + t * (c - b); }
            var denom = 1f / (va + vb + vc);
            float v = vb * denom, w = vc * denom;
            bary = new Vector3(1 - v - w, v, w);
            return a + ab * v + ac * w;
        }
    }
}
