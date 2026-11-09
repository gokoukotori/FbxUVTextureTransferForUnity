using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;

namespace GokouKotori.FBXUVTextureTransfer
{
    // Correction pins (spec §5.2), placed in target UV but acting in 3D. The field u(X) = Σ_k φ(|X − c_k| / r_k) · w_k
    // (Wendland C2) is solved so that u(P_k) = P_k − Q_k at moving pins and u(Q_k) = 0 at fixed pins. A texel at X reads
    // the source at X − u(X) moved along the surface, so a moving pin's position shows what its origin showed. The support
    // is a 3D distance, so both sides of a UV seam move alike.
    internal sealed class FBXUVBodyPinWarp
    {
        internal const int MaxPins = 32;
        internal const float LipschitzLimit = .9f;
        // Centres closer than this make the weight system singular.
        internal const float CoincidenceDistance = .001f;
        // Surface snaps search at least the brief's 2 rings, and enough to reach the known surface point, up to this.
        private const int MaxSnapRings = 8;
        private const float SnapSlack = .001f;
        // An origin on an uncovered texel looks NeighbourReach texels around it: 1/NeighbourResolution UV at any R.
        private const int NeighbourResolution = 1024;
        private const float ContainsTolerance = 1e-4f;
        private const string FlipMessage = "この移動では絵が裏返るため反映できません。移動量を減らすか、影響範囲を広げてください。";

        private readonly FBXUVBodyCorrespondence map;
        private readonly Vector3[] centers;
        private readonly float[] radii;
        private readonly Vector3[] weights;
        private readonly string signature;
        private readonly bool accepted;
        private readonly Vector3 supportMin, supportMax;

        private FBXUVBodyPinWarp(FBXUVBodyCorrespondence map, Vector3[] centers, float[] radii, Vector3[] weights, string signature, double bound)
        {
            this.map = map;
            this.centers = centers;
            this.radii = radii;
            this.weights = weights;
            this.signature = signature;
            // NaN (an unsolvable field) fails this test too.
            accepted = bound < LipschitzLimit;
            LipschitzBound = double.IsNaN(bound) ? float.PositiveInfinity : (float)bound;
            supportMin = Vector3.positiveInfinity;
            supportMax = Vector3.negativeInfinity;
            for (var k = 0; k < centers.Length; k++)
            {
                supportMin = Vector3.Min(supportMin, centers[k] - Vector3.one * radii[k]);
                supportMax = Vector3.Max(supportMax, centers[k] + Vector3.one * radii[k]);
            }
        }

        // Σ_k |w_k| · max|φ'| / r_k bounds the derivative of u; below 1, X → X − u(X) is injective (no flipped picture).
        internal float LipschitzBound { get; }

        // On false, warp is null, except when only the bound failed: then it reports LipschitzBound but cannot be applied.
        internal static bool TryCreate(FBXUVBodyCorrespondence correspondence, IReadOnlyList<FBXUVBodyPin> pins,
            out FBXUVBodyPinWarp warp, out string reason)
        {
            if (correspondence == null) throw new ArgumentNullException(nameof(correspondence));
            warp = null;
            pins = pins ?? Array.Empty<FBXUVBodyPin>();
            reason = ValidateValues(pins);
            if (reason != null) return false;

            var count = pins.Count;
            var centers = new Vector3[count];
            var radii = new float[count];
            var moves = new Vector3[count];
            var target = correspondence.Target;
            for (var k = 0; k < count; k++)
            {
                var pin = pins[k];
                radii[k] = pin.radius;
                if (!TryOrigin(correspondence, pin.origin, out var triangle, out var q, out var normal))
                {
                    reason = "補正ピン " + (k + 1) + " の位置が転送先のボディ上にありません。";
                    return false;
                }
                if (pin.fixedPoint)
                {
                    centers[k] = q;
                    continue;
                }
                // Q + J·(position − origin): the origin triangle's UV → 3D map extended linearly, since the position may
                // lie off every island (a drag across a seam); then onto the surface.
                var moved = Interpolate(target.Positions, target.Triangles, triangle, Barycentric(target, triangle, pin.position));
                Snap(correspondence, moved, normal, Vector3.Distance(moved, q), pin.radius, out var p, out _, out _, out _);
                centers[k] = p;
                moves[k] = p - q;
            }

            for (var a = 0; a < count; a++)
            for (var b = a + 1; b < count; b++)
            {
                if ((centers[a] - centers[b]).sqrMagnitude >= CoincidenceDistance * CoincidenceDistance) continue;
                reason = Overlap(a, b);
                return false;
            }

            var system = new double[count, count + 3];
            for (var i = 0; i < count; i++)
            {
                for (var j = 0; j < count; j++) system[i, j] = FBXUVBodyPinSolver.Kernel((centers[i] - centers[j]).magnitude / (double)radii[j]);
                system[i, count] = moves[i].x;
                system[i, count + 1] = moves[i].y;
                system[i, count + 2] = moves[i].z;
            }
            if (!FBXUVBodyPinSolver.Solve(system, count))
            {
                ClosestPair(centers, out var a, out var b);
                reason = Overlap(a, b);
                return false;
            }

            var weights = new Vector3[count];
            var bound = 0.0;
            for (var k = 0; k < count; k++)
            {
                double x = system[k, count], y = system[k, count + 1], z = system[k, count + 2];
                weights[k] = new Vector3((float)x, (float)y, (float)z);
                bound += Math.Sqrt(x * x + y * y + z * z) * FBXUVBodyPinSolver.KernelSlope / radii[k];
            }
            warp = new FBXUVBodyPinWarp(correspondence, centers, radii, weights, Signature(pins), bound);
            reason = warp.accepted ? string.Empty : FlipMessage;
            return warp.accepted;
        }

        // The checks that need no correspondence, so validation can report them while other settings are still wrong.
        internal static string ValidateValues(IReadOnlyList<FBXUVBodyPin> pins)
        {
            if (pins == null) return null;
            if (pins.Count > MaxPins) return "補正ピンが多すぎます（最大" + MaxPins + "個）。";
            for (var k = 0; k < pins.Count; k++)
            {
                var pin = pins[k];
                if (pin == null || !FBXUVMath.Finite(pin.origin.x) || !FBXUVMath.Finite(pin.origin.y) || !FBXUVMath.Finite(pin.position.x) || !FBXUVMath.Finite(pin.position.y)
                    || !FBXUVMath.Finite(pin.radius) || !(pin.radius > 0f))
                    return "補正ピン " + (k + 1) + " の値が不正です。";
            }
            return null;
        }

        // Spec §5.2: a pin on left/right-shared UV acts on the first triangle only, and says so.
        internal static List<string> SharedUvWarnings(FBXUVBodyCorrespondence correspondence, IReadOnlyList<FBXUVBodyPin> pins)
        {
            if (correspondence == null) throw new ArgumentNullException(nameof(correspondence));
            var warnings = new List<string>();
            if (pins == null || correspondence.TargetAmbiguous == null) return warnings;
            for (var k = 0; k < pins.Count; k++)
            {
                if (pins[k] == null) continue;
                var index = OriginTexel(correspondence, pins[k].origin);
                if (index >= 0 && correspondence.TargetAmbiguous[index])
                    warnings.Add("補正ピン " + (k + 1) + " は左右で共有されたUVの上にあります。片側だけに効きます。");
            }
            return warnings;
        }

        internal Vector3 Displacement(Vector3 point) => Displacement(point, out _);

        // Rewrites SourceUv/Valid for these pins. The map is shared by every layer of the avatar pair, so Base* is restored
        // first where the previous pins changed it; then every texel within r_k of a pin is looked up again. Returns the
        // texel rectangle that changed (the previous and the new one), for UploadRegion; empty when these pins are applied already.
        internal RectInt Apply(FBXUVBodyCorrespondence correspondence)
        {
            if (!ReferenceEquals(correspondence, map)) throw new ArgumentException("補正ピンは作成した対応マップにだけ適用できます。", nameof(correspondence));
            if (!accepted) throw new InvalidOperationException(FlipMessage);
            if (map.AppliedPinSignature == signature) return default;
            var texels = Select(out var rect);
            var previous = map.AppliedPinRect;
            var changed = Union(previous, rect);
            // Until the rewrite completes the record covers both rectangles, so an interrupted rewrite is still undone later.
            map.AppliedPinSignature = null;
            map.AppliedPinRect = changed;
            RestoreBase(map, previous);
            Rewrite(texels);
            map.AppliedPinSignature = signature;
            map.AppliedPinRect = rect;
            return changed;
        }

        // Puts Base* back wherever pins changed the map. Returns the rectangle to upload (empty when there were no pins).
        internal static RectInt Restore(FBXUVBodyCorrespondence correspondence)
        {
            if (correspondence == null) throw new ArgumentNullException(nameof(correspondence));
            var rect = correspondence.AppliedPinRect;
            RestoreBase(correspondence, rect);
            correspondence.AppliedPinSignature = null;
            correspondence.AppliedPinRect = default;
            return rect;
        }

        private List<int> Select(out RectInt rect)
        {
            var result = new List<int>();
            int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;
            var resolution = map.Resolution;
            for (var i = 0; i < map.Texels.Length; i++)
            {
                if (!InSupport(map.TargetPositions[i])) continue;
                result.Add(i);
                int x = map.Texels[i] % resolution, y = map.Texels[i] / resolution;
                minX = Math.Min(minX, x);
                minY = Math.Min(minY, y);
                maxX = Math.Max(maxX, x);
                maxY = Math.Max(maxY, y);
            }
            rect = result.Count == 0 ? default : new RectInt(minX, minY, maxX - minX + 1, maxY - minY + 1);
            return result;
        }

        private bool InSupport(Vector3 point)
        {
            if (point.x < supportMin.x || point.y < supportMin.y || point.z < supportMin.z
                || point.x > supportMax.x || point.y > supportMax.y || point.z > supportMax.z) return false;
            for (var k = 0; k < centers.Length; k++)
            {
                if ((point - centers[k]).sqrMagnitude < radii[k] * radii[k]) return true;
            }
            return false;
        }

        private void Rewrite(List<int> texels)
        {
            var source = map.Source;
            var sourceDistance = FBXUVBodyCorrespondence.MaxDistance * map.Target.SizeFactor;
            Parallel.For(0, texels.Count, n =>
            {
                var i = texels[n];
                var point = map.TargetPositions[i];
                var normal = map.TargetNormals[i];
                var u = Displacement(point, out var radius);
                var tangent = u - Vector3.Dot(u, normal) * normal;
                // Back onto the target surface, with that surface's normal: on curved parts (arms) a point moved along the
                // tangent plane turns far from X's normal, and searching the source with X's normal would leave holes.
                var snapped = Snap(map, point - tangent, normal, tangent.magnitude, radius, out var moved, out var movedNormal, out var onTriangle, out var onBary);
                // 2026-11-06 region spec §4.5: where the moved point lands on a target region triangle, read it with the
                // region-to-region copy, as Build wrote Base* (the rim band's regular point is searched from the moved point).
                if (snapped && map.RegionTransfer != null && map.RegionTransfer.TrySourceUv(map, onTriangle, onBary, moved, movedNormal, out var regionUv))
                {
                    map.SourceUv[i] = regionUv;
                    map.Valid[i] = true;
                }
                // The same search as FBXUVBodyCorrespondence.Build (normal within 60°, 45 mm × SizeFactor).
                else if (map.SourceGrid.Nearest(moved, movedNormal, FBXUVBodyCorrespondence.MaxRing, sourceDistance, out var triangle, out var bary, out _))
                {
                    map.SourceUv[i] = Interpolate(source.Uvs, source.Triangles, triangle, bary);
                    map.Valid[i] = true;
                }
                else
                {
                    map.SourceUv[i] = Vector2.zero;
                    map.Valid[i] = false;
                }
            });
        }

        // radius: the largest radius among the pins whose support contains point (0 outside every support).
        private Vector3 Displacement(Vector3 point, out float radius)
        {
            var sum = Vector3.zero;
            radius = 0f;
            for (var k = 0; k < centers.Length; k++)
            {
                var distance = Vector3.Distance(point, centers[k]);
                if (distance >= radii[k]) continue;
                sum += weights[k] * (float)FBXUVBodyPinSolver.Kernel(distance / radii[k]);
                radius = Mathf.Max(radius, radii[k]);
            }
            return sum;
        }

        // Q: the surface point under the origin UV, on the triangle that owns the origin's texel.
        private static bool TryOrigin(FBXUVBodyCorrespondence map, Vector2 uv, out int triangle, out Vector3 point, out Vector3 normal)
        {
            triangle = -1;
            point = default;
            normal = default;
            var index = OriginTexel(map, uv);
            if (index < 0) return false;
            triangle = map.TargetTriangle[index];
            var bary = Barycentric(map.Target, triangle, uv);
            point = Interpolate(map.Target.Positions, map.Target.Triangles, triangle, bary);
            normal = FBXUVBodySpatialGrid.Normalize(Interpolate(map.Target.Normals, map.Target.Triangles, triangle, bary));
            if (normal == Vector3.zero) normal = map.TargetNormals[index];
            return FBXUVMath.Finite(point.x) && FBXUVMath.Finite(point.y) && FBXUVMath.Finite(point.z);
        }

        // The covered texel that contains uv, or -1 (off the map or off every island). A texel is covered when its centre is on
        // a target triangle, so next to an island edge the texel under uv can be covered at one R and not at another: the pin
        // editor and the TTT preview use R = 1024, the build min(canvas, 2048). When it is not covered, the covered texels
        // within NeighbourReach (1/1024 UV at any R ≤ 2048) are tried: the one whose triangle contains uv, else the nearest. So
        // a pin the editor accepts is accepted by the build, and the other way round.
        private static int OriginTexel(FBXUVBodyCorrespondence map, Vector2 uv)
        {
            var resolution = map.Resolution;
            if (!FBXUVMath.Finite(uv.x) || !FBXUVMath.Finite(uv.y)) return -1;
            var x = Mathf.FloorToInt(uv.x * resolution);
            var y = Mathf.FloorToInt(uv.y * resolution);
            if (x < 0 || y < 0 || x >= resolution || y >= resolution) return -1;
            var exact = map.IndexOfTexel(y * resolution + x);
            if (exact >= 0) return exact;

            var reach = NeighbourReach(resolution);
            var nearest = -1;
            var nearestDistance = float.PositiveInfinity;
            for (var ny = Math.Max(0, y - reach); ny <= Math.Min(resolution - 1, y + reach); ny++)
            for (var nx = Math.Max(0, x - reach); nx <= Math.Min(resolution - 1, x + reach); nx++)
            {
                var index = map.IndexOfTexel(ny * resolution + nx);
                if (index < 0) continue;
                if (Contains(map.Target, map.TargetTriangle[index], uv)) return index;
                var distance = (new Vector2((nx + .5f) / resolution, (ny + .5f) / resolution) - uv).sqrMagnitude;
                if (distance >= nearestDistance) continue;
                nearestDistance = distance;
                nearest = index;
            }
            return nearest;
        }

        // Texels searched around an uncovered origin texel: 1 up to R = 1024, 2 at R = 2048.
        private static int NeighbourReach(int resolution) => Math.Max(1, (resolution + NeighbourResolution - 1) / NeighbourResolution);

        private static bool Contains(FBXUVBodyGeometry geometry, int triangle, Vector2 uv)
        {
            var bary = Barycentric(geometry, triangle, uv);
            return bary.x >= -ContainsTolerance && bary.y >= -ContainsTolerance && bary.z >= -ContainsTolerance;
        }

        // The target UV whose surface point these pins show at uv: X (the point under uv) moved back by the tangential part of
        // u(X) and snapped onto the surface, as Rewrite reads it, then the UV of the target triangle found there. A new moving
        // pin with this origin and position = uv keeps u(X) (the old field already satisfies its constraint), so adding it
        // leaves the picture as it is, like FBXUVEyeManualEditor's inverse placement. Outside every support it is uv itself.
        // False when uv is off the body or the moved point finds no target surface.
        internal bool TryInverseUv(Vector2 uv, out Vector2 origin)
        {
            origin = uv;
            if (!TryOrigin(map, uv, out _, out var point, out var normal)) return false;
            var u = Displacement(point, out var radius);
            if (radius <= 0f) return true;
            var tangent = u - Vector3.Dot(u, normal) * normal;
            if (!Snap(map, point - tangent, normal, tangent.magnitude, radius, out _, out _, out var triangle, out var bary)) return false;
            var found = Interpolate(map.Target.Uvs, map.Target.Triangles, triangle, bary);
            if (!FBXUVMath.Finite(found.x) || !FBXUVMath.Finite(found.y)) return false;
            origin = found;
            return true;
        }

        // The nearest target surface point whose face is within 60° of normal, and the interpolated normal there; the point
        // itself when none is found (false). reach is the distance to a surface point known to be near (X, or Q for a pin),
        // and the rings searched cover it; limit is the search limit (the pin radius).
        private static bool Snap(FBXUVBodyCorrespondence map, Vector3 point, Vector3 normal, float reach, float limit,
            out Vector3 snapped, out Vector3 snappedNormal, out int triangle, out Vector3 bary)
        {
            snapped = point;
            snappedNormal = normal;
            var target = map.Target;
            var cell = FBXUVBodyCorrespondence.TargetCellSize * target.SizeFactor;
            var rings = Mathf.Clamp(Mathf.CeilToInt((reach + SnapSlack) / cell), 2, MaxSnapRings);
            if (!map.TargetGrid.Nearest(point, normal, rings, Mathf.Max(limit, reach + SnapSlack), out triangle, out bary, out _)) return false;
            snapped = Interpolate(target.Positions, target.Triangles, triangle, bary);
            var interpolated = FBXUVBodySpatialGrid.Normalize(Interpolate(target.Normals, target.Triangles, triangle, bary));
            if (interpolated != Vector3.zero) snappedNormal = interpolated;
            return true;
        }

        // Unclamped barycentric coordinates of uv in the triangle's UVs; outside the triangle they extrapolate linearly.
        private static Vector3 Barycentric(FBXUVBodyGeometry geometry, int triangle, Vector2 uv)
        {
            var a = geometry.Uvs[geometry.Triangles[triangle * 3]];
            var b = geometry.Uvs[geometry.Triangles[triangle * 3 + 1]];
            var c = geometry.Uvs[geometry.Triangles[triangle * 3 + 2]];
            double d1x = b.x - a.x, d1y = b.y - a.y, d2x = c.x - a.x, d2y = c.y - a.y, vx = uv.x - a.x, vy = uv.y - a.y;
            var det = d1x * d2y - d2x * d1y;
            var w1 = (vx * d2y - d2x * vy) / det;
            var w2 = (d1x * vy - vx * d1y) / det;
            return new Vector3((float)(1.0 - w1 - w2), (float)w1, (float)w2);
        }

        private static Vector3 Interpolate(Vector3[] values, int[] triangles, int triangle, Vector3 bary)
            => values[triangles[triangle * 3]] * bary.x + values[triangles[triangle * 3 + 1]] * bary.y + values[triangles[triangle * 3 + 2]] * bary.z;

        private static Vector2 Interpolate(Vector2[] values, int[] triangles, int triangle, Vector3 bary)
            => values[triangles[triangle * 3]] * bary.x + values[triangles[triangle * 3 + 1]] * bary.y + values[triangles[triangle * 3 + 2]] * bary.z;

        // Restores Base* inside rect, one texel row at a time (Texels is ascending y·R + x).
        private static void RestoreBase(FBXUVBodyCorrespondence map, RectInt rect)
        {
            if (rect.width <= 0 || rect.height <= 0) return;
            var resolution = map.Resolution;
            var texels = map.Texels;
            for (var y = Math.Max(0, rect.yMin); y < Math.Min(resolution, rect.yMax); y++)
            {
                var first = y * resolution + Math.Max(0, rect.xMin);
                var end = y * resolution + Math.Min(resolution, rect.xMax);
                var index = Array.BinarySearch(texels, first);
                if (index < 0) index = ~index;
                for (; index < texels.Length && texels[index] < end; index++)
                {
                    map.SourceUv[index] = map.BaseSourceUv[index];
                    map.Valid[index] = map.BaseValid[index];
                }
            }
        }

        private static RectInt Union(RectInt a, RectInt b)
        {
            if (a.width <= 0 || a.height <= 0) return b;
            if (b.width <= 0 || b.height <= 0) return a;
            int xMin = Math.Min(a.xMin, b.xMin), yMin = Math.Min(a.yMin, b.yMin);
            return new RectInt(xMin, yMin, Math.Max(a.xMax, b.xMax) - xMin, Math.Max(a.yMax, b.yMax) - yMin);
        }

        // The exact pin values: layers with equal pins produce the same map, so the second one has nothing to redo.
        private static string Signature(IReadOnlyList<FBXUVBodyPin> pins)
        {
            var builder = new StringBuilder(pins.Count * 41);
            foreach (var pin in pins)
            {
                builder.Append(pin.fixedPoint ? 'F' : 'M');
                foreach (var value in new[] { pin.origin.x, pin.origin.y, pin.position.x, pin.position.y, pin.radius })
                    builder.Append(BitConverter.ToInt32(BitConverter.GetBytes(value), 0).ToString("X8"));
            }
            return builder.ToString();
        }

        private static void ClosestPair(Vector3[] points, out int first, out int second)
        {
            first = 0;
            second = 1;
            var best = float.PositiveInfinity;
            for (var a = 0; a < points.Length; a++)
            for (var b = a + 1; b < points.Length; b++)
            {
                var distance = (points[a] - points[b]).sqrMagnitude;
                if (distance >= best) continue;
                best = distance;
                first = a;
                second = b;
            }
        }

        private static string Overlap(int a, int b) => "補正ピン " + (a + 1) + " と " + (b + 1) + " の位置が重なっています。";
    }
}
