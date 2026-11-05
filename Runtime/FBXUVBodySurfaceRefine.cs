using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;

namespace GokouKotori.FBXUVTextureTransfer
{
    // A source vertex held on a fixed point of the target during the refine (the nipple landmarks).
    internal struct FBXUVBodyAnchor
    {
        internal int SourceVertex;
        internal Vector3 TargetPoint;
    }

    // 骨格合わせ後の転送元を、転送先の表面へなめらかに寄せる（簡易な非剛体 ICP）。
    // UV シームで分かれた頂点は位置で束ねて同じ変位を与え、面に裂け目を作らない。
    internal static class FBXUVBodySurfaceRefine
    {
        private const int Iterations = 10;
        private const int SmoothingPasses = 30;
        private const float Step = 0.6f;
        private const float CellMeters = 0.02f;
        private const int MaxRing = 4;
        private const float MinNormalDot = 0.5f;

        internal static int CountWeldGroups(FBXUVBodyGeometry source)
        {
            FBXUVBodyWeld.Groups(source, out var count);
            return count;
        }

        internal static Vector3[] Refine(FBXUVBodyGeometry source, Vector3[] aligned, FBXUVBodyGeometry target, IReadOnlyList<FBXUVBodyAnchor> anchors = null)
        {
            var sourcePositions = source.Positions;
            var triangles = source.Triangles;
            var cell = CellMeters * target.SizeFactor;
            var maxDistance = cell * MaxRing;
            var targetFaceNormals = FBXUVBodySpatialGrid.FaceNormals(target.Positions, target.Positions, target.Normals, target.Triangles);
            var grid = new FBXUVBodySpatialGrid(target.Positions, target.Triangles, targetFaceNormals, cell);

            var weldOf = FBXUVBodyWeld.Groups(source, out var weldCount);

            var current = new Vector3[weldCount];
            var counts = new int[weldCount];
            for (var v = 0; v < sourcePositions.Length; v++)
            {
                current[weldOf[v]] += aligned[v];
                counts[weldOf[v]]++;
            }
            for (var w = 0; w < weldCount; w++) current[w] /= Mathf.Max(counts[w], 1);

            Vector3[] anchorTarget = null;
            bool[] anchored = null;
            if (anchors != null && anchors.Count > 0)
            {
                anchorTarget = new Vector3[weldCount];
                anchored = new bool[weldCount];
                foreach (var anchor in anchors)
                {
                    var group = weldOf[anchor.SourceVertex];
                    anchorTarget[group] = anchor.TargetPoint;
                    anchored[group] = true;
                }
            }

            var faceSign = new float[triangles.Length / 3];
            for (var f = 0; f < faceSign.Length; f++)
            {
                int a = triangles[f * 3], b = triangles[f * 3 + 1], c = triangles[f * 3 + 2];
                var original = Vector3.Cross(sourcePositions[b] - sourcePositions[a], sourcePositions[c] - sourcePositions[a]);
                faceSign[f] = Vector3.Dot(original, source.Normals[a] + source.Normals[b] + source.Normals[c]) >= 0f ? 1f : -1f;
            }
            var adjacency = FBXUVBodyWeld.Adjacency(triangles, weldOf, weldCount);
            var active = Enumerable.Range(0, weldCount).Where(w => adjacency[w].Length > 0).ToArray();

            for (var iteration = 0; iteration < Iterations; iteration++)
            {
                var normals = new Vector3[weldCount];
                for (var f = 0; f < faceSign.Length; f++)
                {
                    int wa = weldOf[triangles[f * 3]], wb = weldOf[triangles[f * 3 + 1]], wc = weldOf[triangles[f * 3 + 2]];
                    var faceNormal = Vector3.Cross(current[wb] - current[wa], current[wc] - current[wa]) * faceSign[f];
                    normals[wa] += faceNormal; normals[wb] += faceNormal; normals[wc] += faceNormal;
                }

                // A fold or crease can leave a welded vertex whose normal matches none of its own faces.
                // Its query would snap to an unrelated surface, so it is treated as not found and filled from its neighbours.
                var reliable = new bool[weldCount];
                for (var f = 0; f < faceSign.Length; f++)
                {
                    int wa = weldOf[triangles[f * 3]], wb = weldOf[triangles[f * 3 + 1]], wc = weldOf[triangles[f * 3 + 2]];
                    var faceNormal = FBXUVBodySpatialGrid.Normalize(Vector3.Cross(current[wb] - current[wa], current[wc] - current[wa]) * faceSign[f]);
                    if (Vector3.Dot(faceNormal, FBXUVBodySpatialGrid.Normalize(normals[wa])) >= MinNormalDot) reliable[wa] = true;
                    if (Vector3.Dot(faceNormal, FBXUVBodySpatialGrid.Normalize(normals[wb])) >= MinNormalDot) reliable[wb] = true;
                    if (Vector3.Dot(faceNormal, FBXUVBodySpatialGrid.Normalize(normals[wc])) >= MinNormalDot) reliable[wc] = true;
                }

                var raw = new Vector3[weldCount];
                var has = new float[weldCount];
                Parallel.For(0, active.Length, i =>
                {
                    var w = active[i];
                    if (anchored != null && anchored[w])
                    {
                        raw[w] = anchorTarget[w] - current[w];
                        has[w] = 1f;
                        return;
                    }
                    if (!reliable[w]) return;
                    if (!grid.Nearest(current[w], FBXUVBodySpatialGrid.Normalize(normals[w]), MaxRing, maxDistance, out var triangle, out var bary, out _)) return;
                    var closest = target.Positions[target.Triangles[triangle * 3]] * bary.x
                        + target.Positions[target.Triangles[triangle * 3 + 1]] * bary.y
                        + target.Positions[target.Triangles[triangle * 3 + 2]] * bary.z;
                    raw[w] = closest - current[w];
                    has[w] = 1f;
                });

                var displacement = (Vector3[])raw.Clone();
                for (var pass = 0; pass < SmoothingPasses; pass++)
                {
                    var next = new Vector3[weldCount];
                    foreach (var w in active)
                    {
                        var sum = raw[w] * has[w];
                        foreach (var j in adjacency[w]) sum += displacement[j];
                        next[w] = sum / (has[w] + adjacency[w].Length);
                    }
                    displacement = next;
                }
                foreach (var w in active) current[w] += displacement[w] * Step;
                if (anchored != null)
                {
                    for (var w = 0; w < weldCount; w++)
                    {
                        if (anchored[w]) current[w] = anchorTarget[w];
                    }
                }
            }

            var result = new Vector3[sourcePositions.Length];
            for (var v = 0; v < result.Length; v++) result[v] = current[weldOf[v]];
            return result;
        }
    }
}
