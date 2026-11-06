using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using UnityEngine;

namespace GokouKotori.FBXUVTextureTransfer
{
    internal static class FBXUVBodyNippleShape
    {
        private static readonly Regex AutoPattern = new Regex(
            @"^(nipple|nip|乳首)[_ ]?on$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        internal static IReadOnlyList<string> CandidateNames(IReadOnlyList<FBXUVTextureSubmeshes> usages)
        {
            var names = new List<string>();
            if (usages == null) return names;
            var seen = new HashSet<string>();
            foreach (var usage in usages)
            {
                var mesh = usage.Mesh;
                if (mesh == null) continue;
                for (var i = 0; i < mesh.blendShapeCount; i++)
                {
                    var name = mesh.GetBlendShapeName(i);
                    if (seen.Add(name)) names.Add(name);
                }
            }
            return names;
        }

        internal static string AutoName(IReadOnlyList<FBXUVTextureSubmeshes> usages)
        {
            foreach (var name in CandidateNames(usages))
            {
                if (AutoPattern.IsMatch(name)) return name;
            }
            return null;
        }

        internal static string Resolve(IReadOnlyList<FBXUVTextureSubmeshes> usages, FBXUVBodyNippleShapeSelection selection, out bool missing)
        {
            missing = false;
            if (selection == null || selection.mode == FBXUVBodyNippleShapeMode.Auto) return AutoName(usages);
            if (selection.mode == FBXUVBodyNippleShapeMode.None) return null;

            var name = selection.shapeName;
            if (!string.IsNullOrEmpty(name))
            {
                foreach (var candidate in CandidateNames(usages))
                {
                    if (candidate == name) return name;
                }
            }
            missing = true;
            return null;
        }

        internal static IReadOnlyList<string> CandidateNames(GameObject modelRoot, Texture bodyTexture)
            => CandidateNames(FBXUVTextureUsage.Collect(modelRoot, bodyTexture));

        internal static string AutoName(GameObject modelRoot, Texture bodyTexture)
            => AutoName(FBXUVTextureUsage.Collect(modelRoot, bodyTexture));

        internal static string Resolve(GameObject modelRoot, Texture bodyTexture, FBXUVBodyNippleShapeSelection selection, out bool missing)
            => Resolve(FBXUVTextureUsage.Collect(modelRoot, bodyTexture), selection, out missing);

        internal static float[] ReadDeltas(Mesh mesh, int shapeIndex)
        {
            var count = mesh.vertexCount;
            var result = new float[count];
            if (shapeIndex < 0 || shapeIndex >= mesh.blendShapeCount) return result;
            var frames = mesh.GetBlendShapeFrameCount(shapeIndex);
            if (frames <= 0) return result;
            var deltaVertices = new Vector3[count];
            var deltaNormals = new Vector3[count];
            var deltaTangents = new Vector3[count];
            mesh.GetBlendShapeFrameVertices(shapeIndex, frames - 1, deltaVertices, deltaNormals, deltaTangents);
            for (var i = 0; i < count; i++) result[i] = deltaVertices[i].magnitude;
            return result;
        }

        // 範囲の値は 2026-11-06 の実測で確定済み（Logs/BodyNippleCheck-20261105/shape-regions-area.md）。
        private const float DeltaFraction = 0.02f;
        private const float ClusterRadius = 0.04f;
        private const int MinimumTriangles = 20;
        private const float MirrorTolerance = 0.005f;
        private const float RadiusTolerance = 0.1f;

        // 動く量から、左右の乳輪の範囲を取り出す（spec §3.3）。
        internal static FBXUVBodyNippleRegions Extract(FBXUVBodyGeometry geometry)
        {
            var result = new FBXUVBodyNippleRegions();
            if (geometry == null || geometry.NippleShapeDeltas == null || geometry.Positions == null || geometry.Triangles == null
                || geometry.Joints == null || !geometry.Joints.TryGetValue((int)HumanBodyBones.Hips, out var hips)) return result;
            var positions = geometry.Positions;
            var deltas = geometry.NippleShapeDeltas;
            var vertexCount = Math.Min(positions.Length, deltas.Length);
            var name = geometry.NippleShapeName;
            var notAligned = $"シェイプキー {name} の範囲が左右そろっていません";

            var referenced = new bool[vertexCount];
            foreach (var index in geometry.Triangles)
            {
                if (index >= 0 && index < vertexCount) referenced[index] = true;
            }

            var maximum = 0f;
            for (var v = 0; v < vertexCount; v++) maximum = Mathf.Max(maximum, deltas[v]);
            if (!(maximum > 0f))
            {
                result.Reason = notAligned;
                return result;
            }

            var threshold = DeltaFraction * maximum;
            var sides = new FBXUVBodyNippleRegion[2];
            for (var s = 0; s < 2; s++)
            {
                // Left is the -X side of the hips.
                var candidates = new List<int>();
                var peak = -1;
                for (var v = 0; v < vertexCount; v++)
                {
                    if (!referenced[v] || deltas[v] < threshold) continue;
                    if ((positions[v].x - hips.x < 0f ? 0 : 1) != s) continue;
                    candidates.Add(v);
                    if (peak < 0 || deltas[v] > deltas[peak]) peak = v;
                }
                if (peak < 0) continue;

                var limit = ClusterRadius * geometry.SizeFactor;
                var inRegion = new bool[vertexCount];
                var members = new List<int>();
                foreach (var v in candidates)
                {
                    if ((positions[v] - positions[peak]).magnitude > limit) continue;
                    inRegion[v] = true;
                    members.Add(v);
                }

                var regionTriangles = new List<int>();
                var triangles = geometry.Triangles;
                var areaSum = 0f;
                var weighted = Vector3.zero;
                for (var t = 0; t + 2 < triangles.Length; t += 3)
                {
                    int a = triangles[t], b = triangles[t + 1], c = triangles[t + 2];
                    if (a < 0 || b < 0 || c < 0 || a >= vertexCount || b >= vertexCount || c >= vertexCount) continue;
                    if (!inRegion[a] || !inRegion[b] || !inRegion[c]) continue;
                    var area = Vector3.Cross(positions[b] - positions[a], positions[c] - positions[a]).magnitude * 0.5f;
                    regionTriangles.Add(t / 3);
                    areaSum += area;
                    weighted += area * (positions[a] + positions[b] + positions[c]) / 3f;
                }

                if (regionTriangles.Count == 0 || !(areaSum > 0f)) continue;
                var centre = weighted / areaSum;
                var landmark = -1;
                var best = float.MaxValue;
                foreach (var v in members)
                {
                    var distance = (positions[v] - centre).sqrMagnitude;
                    if (distance < best)
                    {
                        best = distance;
                        landmark = v;
                    }
                }

                sides[s] = new FBXUVBodyNippleRegion
                {
                    Vertex = landmark,
                    Centre = centre,
                    Radius = Mathf.Sqrt(areaSum / Mathf.PI),
                    Triangles = regionTriangles.ToArray(),
                };
            }

            result.Left = sides[0];
            result.Right = sides[1];
            if (sides[0] == null || sides[1] == null
                || sides[0].Triangles.Length < MinimumTriangles || sides[1].Triangles.Length < MinimumTriangles)
            {
                result.Reason = notAligned;
                return result;
            }

            var mirrored = new Vector3(2f * hips.x - sides[1].Centre.x, sides[1].Centre.y, sides[1].Centre.z);
            var difference = sides[0].Centre - mirrored;
            var tolerance = MirrorTolerance * geometry.SizeFactor;
            var larger = Mathf.Max(sides[0].Radius, sides[1].Radius);
            if (Mathf.Abs(difference.x) > tolerance || Mathf.Abs(difference.y) > tolerance || Mathf.Abs(difference.z) > tolerance
                || Mathf.Abs(sides[0].Radius - sides[1].Radius) > RadiusTolerance * larger)
            {
                result.Reason = $"シェイプキー {name} の範囲が左右で対称ではありません";
                return result;
            }

            result.Found = true;
            return result;
        }

        // 範囲の三角形の面積の和から求めた半径 √(A / π)。
        internal static float RegionRadius(Vector3[] positions, int[] triangles, int[] regionTriangles)
        {
            if (positions == null || triangles == null || regionTriangles == null || regionTriangles.Length == 0) return 0f;
            var area = 0f;
            foreach (var t in regionTriangles)
            {
                var a = positions[triangles[t * 3]];
                var b = positions[triangles[t * 3 + 1]];
                var c = positions[triangles[t * 3 + 2]];
                area += Vector3.Cross(b - a, c - a).magnitude * 0.5f;
            }
            return Mathf.Sqrt(area / Mathf.PI);
        }
    }

    // 片側の乳輪の範囲。Radius は m 単位（サイズ係数で割らない）。
    internal sealed class FBXUVBodyNippleRegion
    {
        internal int Vertex;
        internal Vector3 Centre;
        internal float Radius;
        // geometry.Triangles の 3 つおきの位置 ÷ 3。
        internal int[] Triangles;
    }

    internal sealed class FBXUVBodyNippleRegions
    {
        internal bool Found;
        // 見つからない側は null。
        internal FBXUVBodyNippleRegion Left, Right;
        // 使えない理由の後半（「転送元の」などは呼び出し側が付ける）。シェイプキーが無いときは null。
        internal string Reason;
    }
}
