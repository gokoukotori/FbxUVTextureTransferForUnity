using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;

namespace GokouKotori.FBXUVTextureTransfer
{
    internal sealed class FBXUVBodyTexelRaster
    {
        internal int[] Texels;
        internal int[] Triangle;
        internal Vector3[] Barycentric, Positions, Normals;
        internal bool[] Ambiguous;
        internal int AmbiguousCount;
    }

    internal sealed class FBXUVBodyDiagnostics
    {
        internal const float FoundRateThreshold = 0.98f;
        private const int MaxLowBones = 5;

        internal int Covered, Found, AmbiguousTexels;
        internal float FoundRate;
        internal IReadOnlyList<(string Bone, int Covered, float FoundRate)> ByBone;
        internal IReadOnlyList<string> Warnings;

        internal static IReadOnlyList<string> ComposeWarnings(float foundRate, IReadOnlyList<(string Bone, int Covered, float FoundRate)> byBone, int ambiguousTexels)
        {
            var warnings = new List<string>();
            if (foundRate < FoundRateThreshold)
            {
                var message = "転送元が見つかった割合が " + (foundRate * 100f).ToString("0.0", CultureInfo.InvariantCulture) + "% です（98% 未満）。";
                var low = (byBone ?? new List<(string Bone, int Covered, float FoundRate)>())
                    .Where(b => b.FoundRate < FoundRateThreshold)
                    .OrderByDescending(b => b.Covered)
                    .Take(MaxLowBones)
                    .Select(b => b.Bone)
                    .ToArray();
                if (low.Length > 0) message += "低い部位: " + string.Join(", ", low);
                warnings.Add(message);
            }

            if (ambiguousTexels > 0)
                warnings.Add("転送先のUVが左右で共有されている部分があります（" + ambiguousTexels + " テクセル）。片側だけが使われます。");
            warnings.Add("指先・足先は精度が落ちます。必要なら補正ピンで直してください。");
            return warnings;
        }
    }

    internal sealed class FBXUVBodyCorrespondence
    {
        // Times the target's SizeFactor. FBXUVBodyPinWarp searches again with the same values (spec §5.2).
        internal const float SourceCellSize = 0.015f;
        internal const float TargetCellSize = 0.02f;
        internal const float MaxDistance = 0.045f;
        internal const int MaxRing = 3;
        private const float AmbiguityDistanceSquared = 1e-4f;

        internal int Resolution;
        internal int[] Texels;
        internal Vector3[] TargetPositions;
        internal Vector3[] TargetNormals;
        internal int[] TargetTriangle;
        internal Vector3[] TargetBarycentric;
        internal int[] TargetIsland;
        // Texels where target triangles more than 1 cm apart in 3D share the UV (left/right shared); the first triangle owns them.
        internal bool[] TargetAmbiguous;
        internal Vector2[] BaseSourceUv;
        internal bool[] BaseValid;
        internal Vector2[] SourceUv;
        internal bool[] Valid;
        internal FBXUVBodyGeometry Source, Target;
        internal Vector3[] AlignedSource;
        internal FBXUVBodySpatialGrid SourceGrid, TargetGrid;
        internal FBXUVBodyDiagnostics Diagnostics;
        // Never null: what the nipple landmarks did while this map was built.
        internal FBXUVBodyLandmarkResult Landmarks;
        // The shared map carries the pins of whichever layer applied last. SourceUv/Valid equal Base* outside
        // AppliedPinRect, so the next layer restores only that rectangle (FBXUVBodyPinWarp).
        internal string AppliedPinSignature;
        internal RectInt AppliedPinRect;

        // Skeleton alignment → nipple landmarks (when alignNipples) → surface refinement → map. Whenever the landmarks
        // are not used, the refinement gets the skeleton-aligned shape and no anchors, exactly as with alignNipples off.
        internal static FBXUVBodyCorrespondence Build(FBXUVBodyGeometry source, FBXUVBodyGeometry target, int resolution, bool alignNipples = true)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (target == null) throw new ArgumentNullException(nameof(target));
            var aligned = FBXUVBodySkeletonAlignment.Align(source, target);
            var landmarks = new FBXUVBodyLandmarkResult { Enabled = alignNipples };
            FBXUVBodyNipples sourceNipples = null, targetNipples = null;
            var moves = new Vector3[2];
            Vector3[] refined = null;
            if (!alignNipples)
            {
                landmarks.Reason = FBXUVBodyLandmarks.OffReason;
            }
            else
            {
                sourceNipples = FBXUVBodyLandmarks.Detect(source);
                targetNipples = FBXUVBodyLandmarks.Detect(target);
                landmarks.Reason = NippleReason(sourceNipples, true) ?? NippleReason(targetNipples, false);
                if (landmarks.Reason == null)
                {
                    refined = RefineOnNipples(source, target, aligned, sourceNipples, targetNipples, moves, out var reason);
                    landmarks.Reason = reason;
                    landmarks.Used = refined != null;
                }
            }

            if (refined == null) refined = FBXUVBodySurfaceRefine.Refine(source, aligned, target);
            var map = BuildCore(source, target, resolution, refined);
            map.Landmarks = landmarks;
            if (sourceNipples != null && sourceNipples.Found && targetNipples.Found)
            {
                landmarks.Left = LandmarkSide(map, sourceNipples.Left, targetNipples.Left, moves[0]);
                landmarks.Right = LandmarkSide(map, sourceNipples.Right, targetNipples.Right, moves[1]);
            }

            return map;
        }

        // Skips the skeleton alignment and the surface refinement: the source vertices are used as they are (spec §8.3).
        internal static FBXUVBodyCorrespondence BuildWithoutAlignmentForTesting(FBXUVBodyGeometry source, FBXUVBodyGeometry target, int resolution)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (target == null) throw new ArgumentNullException(nameof(target));
            var map = BuildCore(source, target, resolution, source.Positions);
            map.Landmarks = new FBXUVBodyLandmarkResult { Enabled = false, Reason = FBXUVBodyLandmarks.OffReason };
            return map;
        }

        // The first missing or asymmetric side, source before target and left before right; null when both sides are found.
        private static string NippleReason(FBXUVBodyNipples nipples, bool source)
        {
            switch (nipples.Status)
            {
                case FBXUVBodyNippleStatus.Found: return null;
                case FBXUVBodyNippleStatus.LeftMissing: return FBXUVBodyLandmarks.MissingReason(source, true);
                case FBXUVBodyNippleStatus.RightMissing: return FBXUVBodyLandmarks.MissingReason(source, false);
                default: return FBXUVBodyLandmarks.AsymmetricReason(source);
            }
        }

        // Spec §3.1–3.4. moves[k] = the along-surface offset m = (t − s) − ((t − s)·n) n from the skeleton-aligned source
        // nipple s to the target nipple t (left, right). Returns the refined source when the landmarks are used, otherwise
        // null and the reason; aligned is only read.
        private static Vector3[] RefineOnNipples(FBXUVBodyGeometry source, FBXUVBodyGeometry target, Vector3[] aligned,
            FBXUVBodyNipples sourceNipples, FBXUVBodyNipples targetNipples, Vector3[] moves, out string reason)
        {
            var sizeFactor = target.SizeFactor;
            var centers = new[]
            {
                Mean(aligned, FBXUVBodyLandmarks.GroupMembers(source, sourceNipples.Left)),
                Mean(aligned, FBXUVBodyLandmarks.GroupMembers(source, sourceNipples.Right)),
            };
            var points = new[] { targetNipples.LeftPosition, targetNipples.RightPosition };
            var normals = new[] { targetNipples.LeftNormal, targetNipples.RightNormal };
            for (var k = 0; k < 2; k++)
            {
                var difference = points[k] - centers[k];
                moves[k] = difference - Vector3.Dot(difference, normals[k]) * normals[k];
            }

            var maxOffset = FBXUVBodyLandmarks.MaxOffsetMeters * sizeFactor;
            var minOffset = FBXUVBodyLandmarks.MinOffsetMeters * sizeFactor;
            if (moves[0].magnitude > maxOffset || moves[1].magnitude > maxOffset)
            {
                reason = FBXUVBodyLandmarks.TooFarReason;
                return null;
            }

            if (moves[0].magnitude < minOffset && moves[1].magnitude < minOffset)
            {
                reason = FBXUVBodyLandmarks.NotNeededReason;
                return null;
            }

            if (!FBXUVBodyLandmarkWarp.TryWarp(aligned, centers, moves, FBXUVBodyLandmarkWarp.RadiusMeters * sizeFactor, out var warped, out reason)) return null;
            var anchors = new[]
            {
                new FBXUVBodyAnchor { SourceVertex = sourceNipples.Left, TargetPoint = points[0] },
                new FBXUVBodyAnchor { SourceVertex = sourceNipples.Right, TargetPoint = points[1] },
            };
            return FBXUVBodySurfaceRefine.Refine(source, warped, target, anchors);
        }

        private static FBXUVBodyLandmarkSide LandmarkSide(FBXUVBodyCorrespondence map, int sourceVertex, int targetVertex, Vector3 move)
        {
            return new FBXUVBodyLandmarkSide
            {
                SourceVertex = sourceVertex,
                TargetVertex = targetVertex,
                OffsetBefore = move.magnitude / map.Target.SizeFactor,
                OffsetAfter = MeasureLandmarkOffset(map, sourceVertex, targetVertex),
                TargetUvs = FBXUVBodyLandmarks.GroupUvs(map.Target, targetVertex),
            };
        }

        // Spec §3.5: how far from the source nipple the target nipple reads. Looks up the map's source surface from the
        // target nipple t along its normal n with the map's own limits, takes the hit on the source's original shape
        // (before any warp) and returns its distance to the source nipple, divided by the source's SizeFactor. NaN when
        // nothing is found. Works on any map, including one built with alignNipples off.
        internal static float MeasureLandmarkOffset(FBXUVBodyCorrespondence map, int sourceVertex, int targetVertex)
        {
            if (map == null) throw new ArgumentNullException(nameof(map));
            var targetMembers = FBXUVBodyLandmarks.GroupMembers(map.Target, targetVertex);
            var sourceMembers = FBXUVBodyLandmarks.GroupMembers(map.Source, sourceVertex);
            if (targetMembers.Length == 0 || sourceMembers.Length == 0) return float.NaN;
            var t = Mean(map.Target.Positions, targetMembers);
            var normal = Vector3.zero;
            foreach (var v in targetMembers) normal += map.Target.Normals[v];
            var s = Mean(map.Source.Positions, sourceMembers);
            if (!map.SourceGrid.Nearest(t, normal.normalized, MaxRing, MaxDistance * map.Target.SizeFactor, out var triangle, out var bary, out _))
                return float.NaN;
            var positions = map.Source.Positions;
            var triangles = map.Source.Triangles;
            var point = positions[triangles[triangle * 3]] * bary.x
                + positions[triangles[triangle * 3 + 1]] * bary.y
                + positions[triangles[triangle * 3 + 2]] * bary.z;
            return (point - s).magnitude / map.Source.SizeFactor;
        }

        private static Vector3 Mean(Vector3[] positions, int[] members)
        {
            var sum = Vector3.zero;
            foreach (var v in members) sum += positions[v];
            return sum / members.Length;
        }

        private static FBXUVBodyCorrespondence BuildCore(FBXUVBodyGeometry source, FBXUVBodyGeometry target, int resolution, Vector3[] refined)
        {
            var sizeFactor = target.SizeFactor;
            var raster = Rasterize(target.Positions, target.Normals, target.Uvs, target.Triangles, resolution);

            var sourceFaceNormals = FBXUVBodySpatialGrid.FaceNormals(refined, source.Positions, source.Normals, source.Triangles);
            var sourceGrid = new FBXUVBodySpatialGrid(refined, source.Triangles, sourceFaceNormals, SourceCellSize * sizeFactor);
            var targetFaceNormals = FBXUVBodySpatialGrid.FaceNormals(target.Positions, target.Positions, target.Normals, target.Triangles);
            var targetGrid = new FBXUVBodySpatialGrid(target.Positions, target.Triangles, targetFaceNormals, TargetCellSize * sizeFactor);

            var count = raster.Texels.Length;
            var sourceUv = new Vector2[count];
            var valid = new bool[count];
            var maxDistance = MaxDistance * sizeFactor;
            Parallel.For(0, count, i =>
            {
                if (!sourceGrid.Nearest(raster.Positions[i], raster.Normals[i], MaxRing, maxDistance, out var triangle, out var bary, out _)) return;
                var uvA = source.Uvs[source.Triangles[triangle * 3]];
                var uvB = source.Uvs[source.Triangles[triangle * 3 + 1]];
                var uvC = source.Uvs[source.Triangles[triangle * 3 + 2]];
                sourceUv[i] = uvA * bary.x + uvB * bary.y + uvC * bary.z;
                valid[i] = true;
            });

            var triangleIsland = BuildTriangleIslands(target);
            var island = new int[count];
            for (var i = 0; i < count; i++) island[i] = triangleIsland[raster.Triangle[i]];

            var result = new FBXUVBodyCorrespondence
            {
                Resolution = resolution,
                Texels = raster.Texels,
                TargetPositions = raster.Positions,
                TargetNormals = raster.Normals,
                TargetTriangle = raster.Triangle,
                TargetBarycentric = raster.Barycentric,
                TargetIsland = island,
                TargetAmbiguous = raster.Ambiguous,
                BaseSourceUv = sourceUv,
                BaseValid = valid,
                SourceUv = (Vector2[])sourceUv.Clone(),
                Valid = (bool[])valid.Clone(),
                Source = source,
                Target = target,
                AlignedSource = refined,
                SourceGrid = sourceGrid,
                TargetGrid = targetGrid,
            };
            result.Diagnostics = BuildDiagnostics(result, raster);
            return result;
        }

        internal int IndexOfTexel(int texel)
        {
            var index = Array.BinarySearch(Texels, texel);
            return index >= 0 ? index : -1;
        }

        internal static FBXUVBodyTexelRaster Rasterize(Vector3[] positions, Vector3[] normals, Vector2[] uvs, int[] triangles, int resolution)
        {
            var total = resolution * resolution;
            var owner = new int[total];
            for (var i = 0; i < total; i++) owner[i] = -1;
            var ownerPosition = new Vector3[total];
            var ambiguous = new bool[total];
            var ambiguousCount = 0;
            var covered = 0;

            for (var t = 0; t < triangles.Length / 3; t++)
            {
                int i0 = triangles[t * 3], i1 = triangles[t * 3 + 1], i2 = triangles[t * 3 + 2];
                Vector2 a = uvs[i0] * resolution, b = uvs[i1] * resolution, c = uvs[i2] * resolution;
                var area = Edge(a, b, c);
                if (Mathf.Abs(area) < 1e-9f) continue;
                var minX = Mathf.Max(0, Mathf.FloorToInt(Mathf.Min(a.x, Mathf.Min(b.x, c.x))));
                var maxX = Mathf.Min(resolution - 1, Mathf.CeilToInt(Mathf.Max(a.x, Mathf.Max(b.x, c.x))));
                var minY = Mathf.Max(0, Mathf.FloorToInt(Mathf.Min(a.y, Mathf.Min(b.y, c.y))));
                var maxY = Mathf.Min(resolution - 1, Mathf.CeilToInt(Mathf.Max(a.y, Mathf.Max(b.y, c.y))));
                for (var y = minY; y <= maxY; y++)
                for (var x = minX; x <= maxX; x++)
                {
                    var p = new Vector2(x + 0.5f, y + 0.5f);
                    var w0 = Edge(b, c, p) / area;
                    var w1 = Edge(c, a, p) / area;
                    var w2 = 1f - w0 - w1;
                    if (w0 < -1e-5f || w1 < -1e-5f || w2 < -1e-5f) continue;
                    var position = positions[i0] * w0 + positions[i1] * w1 + positions[i2] * w2;
                    var index = y * resolution + x;
                    if (owner[index] >= 0)
                    {
                        if (!ambiguous[index] && (ownerPosition[index] - position).sqrMagnitude > AmbiguityDistanceSquared)
                        {
                            ambiguous[index] = true;
                            ambiguousCount++;
                        }
                        continue;
                    }

                    owner[index] = t;
                    ownerPosition[index] = position;
                    covered++;
                }
            }

            var raster = new FBXUVBodyTexelRaster
            {
                Texels = new int[covered],
                Triangle = new int[covered],
                Barycentric = new Vector3[covered],
                Positions = new Vector3[covered],
                Normals = new Vector3[covered],
                Ambiguous = new bool[covered],
                AmbiguousCount = ambiguousCount,
            };
            var cursor = 0;
            for (var index = 0; index < total; index++)
            {
                var t = owner[index];
                if (t < 0) continue;
                int i0 = triangles[t * 3], i1 = triangles[t * 3 + 1], i2 = triangles[t * 3 + 2];
                Vector2 a = uvs[i0] * resolution, b = uvs[i1] * resolution, c = uvs[i2] * resolution;
                var area = Edge(a, b, c);
                var p = new Vector2(index % resolution + 0.5f, index / resolution + 0.5f);
                var w0 = Edge(b, c, p) / area;
                var w1 = Edge(c, a, p) / area;
                var w2 = 1f - w0 - w1;
                raster.Texels[cursor] = index;
                raster.Triangle[cursor] = t;
                raster.Barycentric[cursor] = new Vector3(w0, w1, w2);
                raster.Positions[cursor] = ownerPosition[index];
                raster.Normals[cursor] = FBXUVBodySpatialGrid.Normalize(normals[i0] * w0 + normals[i1] * w1 + normals[i2] * w2);
                raster.Ambiguous[cursor] = ambiguous[index];
                cursor++;
            }

            return raster;
        }

        private static float Edge(Vector2 a, Vector2 b, Vector2 p)
        {
            return (b.x - a.x) * (p.y - a.y) - (b.y - a.y) * (p.x - a.x);
        }

        private static int[] BuildTriangleIslands(FBXUVBodyGeometry geometry)
        {
            var result = new int[geometry.Triangles.Length / 3];
            var nextIsland = 0;
            foreach (var part in geometry.Parts)
            {
                var list = new List<FBXUVTriangle>(part.TriangleCount);
                for (var local = 0; local < part.TriangleCount; local++)
                {
                    var global = part.FirstTriangle + local;
                    list.Add(new FBXUVTriangle(global,
                        geometry.Uvs[geometry.Triangles[global * 3]],
                        geometry.Uvs[geometry.Triangles[global * 3 + 1]],
                        geometry.Uvs[geometry.Triangles[global * 3 + 2]]));
                }

                var islands = FBXUVIslandExtractor.Extract(list);
                foreach (var island in islands)
                {
                    foreach (var triangle in island.triangles) result[triangle.index] = nextIsland;
                    nextIsland++;
                }
            }

            return result;
        }

        private static FBXUVBodyDiagnostics BuildDiagnostics(FBXUVBodyCorrespondence map, FBXUVBodyTexelRaster raster)
        {
            var target = map.Target;
            var covered = new Dictionary<int, int>();
            var found = new Dictionary<int, int>();
            var foundTotal = 0;
            for (var i = 0; i < map.Texels.Length; i++)
            {
                var bone = DominantBone(target, map.TargetTriangle[i], map.TargetBarycentric[i]);
                covered.TryGetValue(bone, out var c);
                covered[bone] = c + 1;
                if (!map.BaseValid[i]) continue;
                foundTotal++;
                found.TryGetValue(bone, out var f);
                found[bone] = f + 1;
            }

            var byBone = new List<(string Bone, int Covered, float FoundRate)>();
            foreach (var pair in covered.OrderByDescending(p => p.Value))
            {
                found.TryGetValue(pair.Key, out var f);
                var name = pair.Key >= 0 && pair.Key < HumanTrait.BoneName.Length ? HumanTrait.BoneName[pair.Key] : "Unknown";
                byBone.Add((name, pair.Value, f / (float)pair.Value));
            }

            var rate = map.Texels.Length == 0 ? 0f : foundTotal / (float)map.Texels.Length;
            return new FBXUVBodyDiagnostics
            {
                Covered = map.Texels.Length,
                Found = foundTotal,
                AmbiguousTexels = raster.AmbiguousCount,
                FoundRate = rate,
                ByBone = byBone,
                Warnings = FBXUVBodyDiagnostics.ComposeWarnings(rate, byBone, raster.AmbiguousCount),
            };
        }

        internal static int DominantBone(FBXUVBodyGeometry geometry, int triangle, Vector3 barycentric)
        {
            var corner = barycentric.x >= barycentric.y && barycentric.x >= barycentric.z ? 0 : (barycentric.y >= barycentric.z ? 1 : 2);
            return DominantBoneOfVertex(geometry, geometry.Triangles[triangle * 3 + corner]);
        }

        internal static int DominantBoneOfVertex(FBXUVBodyGeometry geometry, int vertex)
        {
            var best = -1;
            var bestWeight = float.NegativeInfinity;
            for (var k = 0; k < 4; k++)
            {
                var weight = geometry.InfluenceWeights[vertex * 4 + k];
                if (weight > bestWeight)
                {
                    bestWeight = weight;
                    best = geometry.InfluenceBones[vertex * 4 + k];
                }
            }

            return best;
        }
    }
}
