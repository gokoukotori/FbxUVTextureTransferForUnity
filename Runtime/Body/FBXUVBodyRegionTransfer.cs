using System;
using System.Collections.Generic;
using UnityEngine;

namespace GokouKotori.FBXUVTextureTransfer
{
    // 乳首のシェイプキーの範囲どうしで画素を写す（2026-11-06 範囲どうしの設計書 §4）。
    internal static class FBXUVBodyRegionTransfer
    {
        // |p| がこれ以上の画素は、今の対応づけの点と混ぜる（§4.3）。
        internal const float BlendStart = 0.85f;
        private const float BlendWidth = 0.15f;
        // 円盤上で見つからないときの探し直し（§4.2）。
        private static readonly float[] Retries = { 0.99f, 0.98f, 0.97f };

        // 円盤側の割合（§4.3）：|p| ≤ 0.85 で 1、1.0 以上で 0、その間は 1 − smoothstep。
        internal static float BlendWeight(float radius)
        {
            if (!(radius > BlendStart)) return 1f;
            var s = Mathf.Min(1f, (radius - BlendStart) / BlendWidth);
            return 1f - (3f * s * s - 2f * s * s * s);
        }

        // §4.2 の 1・2：画素の重心座標で転送先の円盤座標を混ぜて p を出し、転送元の円盤で p を含む三角形を探す。
        // 見つからなければ p を 0.99・0.98・0.97 倍にして探し直す。disc は画素の円盤上の位置 p（倍率を掛ける前）。
        // targetTriangle が転送先の範囲の三角形（3 頂点とも範囲の束）でなければ false。
        internal static bool TryMap(FBXUVBodyRegionDisc target, FBXUVBodyRegionDisc source, FBXUVBodyGeometry targetGeometry,
            FBXUVBodyGeometry sourceGeometry, int targetTriangle, Vector3 barycentric, out int sourceTriangle, out Vector3 sourceBarycentric,
            out Vector2 disc)
        {
            if (target == null) throw new ArgumentNullException(nameof(target));
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (targetGeometry == null) throw new ArgumentNullException(nameof(targetGeometry));
            if (sourceGeometry == null) throw new ArgumentNullException(nameof(sourceGeometry));
            sourceTriangle = -1;
            sourceBarycentric = Vector3.zero;
            disc = Vector2.zero;
            var triangles = targetGeometry.Triangles;
            if (targetTriangle < 0 || targetTriangle * 3 + 2 >= triangles.Length) return false;
            Vector2 a, b, c;
            if (!TryCoordinate(target, triangles[targetTriangle * 3], out a)
                || !TryCoordinate(target, triangles[targetTriangle * 3 + 1], out b)
                || !TryCoordinate(target, triangles[targetTriangle * 3 + 2], out c)) return false;

            disc = a * barycentric.x + b * barycentric.y + c * barycentric.z;
            if (source.TryLocate(disc, out sourceTriangle, out sourceBarycentric)) return true;
            foreach (var scale in Retries)
            {
                if (source.TryLocate(disc * scale, out sourceTriangle, out sourceBarycentric)) return true;
            }

            // それでも見つからないとき（転送元の縁が粗く、弦が 0.97 より内側に入るとき）は、原点から p への半直線が
            // 転送元の縁の多角形と交わる半径の (1 − 1e-4) 倍の点で探す。縁は中心から見て星形。
            if (RimRadius(source, disc, out var rimRadius)
                && source.TryLocate(disc.normalized * (rimRadius * (1f - 1e-4f)), out sourceTriangle, out sourceBarycentric)) return true;

            sourceTriangle = -1;
            sourceBarycentric = Vector3.zero;
            return false;
        }

        // 原点から direction の向きへの半直線と、転送元の縁（隣どうしの束の円盤座標を結ぶ多角形）が交わる原点からの距離。
        private static bool RimRadius(FBXUVBodyRegionDisc source, Vector2 direction, out float radius)
        {
            radius = 0f;
            var length = direction.magnitude;
            if (!(length > 0f)) return false;
            var d = direction / length;
            var rim = source.Rim;
            var best = float.MaxValue;
            for (var k = 0; k < rim.Length; k++)
            {
                var a = source.Coordinates[rim[k]];
                var b = source.Coordinates[rim[(k + 1) % rim.Length]];
                var e = b - a;
                // t d = a + u e を解く。
                var denominator = d.x * e.y - d.y * e.x;
                if (Mathf.Abs(denominator) < 1e-12f) continue;
                var t = (a.x * e.y - a.y * e.x) / denominator;
                var u = (a.x * d.y - a.y * d.x) / denominator;
                if (t > 0f && u >= -1e-6f && u <= 1f + 1e-6f && t < best) best = t;
            }

            if (best == float.MaxValue) return false;
            radius = best;
            return true;
        }

        private static bool TryCoordinate(FBXUVBodyRegionDisc disc, int vertex, out Vector2 coordinate)
        {
            coordinate = Vector2.zero;
            if (vertex < 0 || vertex >= disc.WeldOf.Length) return false;
            return disc.Coordinates.TryGetValue(disc.WeldOf[vertex], out coordinate);
        }
    }

    // 範囲どうしで写すときの、転送元・転送先の左右の円盤（§4.4）。対応マップの RegionTransfer に持たせる。
    internal sealed class FBXUVBodyRegionMapping
    {
        internal readonly FBXUVBodyRegionDisc SourceLeft, SourceRight, TargetLeft, TargetRight;
        // 転送先の範囲の三角形 → 左か（true）右か（false）。
        private readonly Dictionary<int, bool> targetSide;

        internal FBXUVBodyRegionMapping(FBXUVBodyRegionDisc sourceLeft, FBXUVBodyRegionDisc sourceRight, FBXUVBodyRegionDisc targetLeft,
            FBXUVBodyRegionDisc targetRight)
        {
            SourceLeft = sourceLeft ?? throw new ArgumentNullException(nameof(sourceLeft));
            SourceRight = sourceRight ?? throw new ArgumentNullException(nameof(sourceRight));
            TargetLeft = targetLeft ?? throw new ArgumentNullException(nameof(targetLeft));
            TargetRight = targetRight ?? throw new ArgumentNullException(nameof(targetRight));
            targetSide = new Dictionary<int, bool>(targetLeft.Triangles.Length + targetRight.Triangles.Length);
            foreach (var t in targetRight.Triangles) targetSide[t] = false;
            foreach (var t in targetLeft.Triangles) targetSide[t] = true;
        }

        // 転送先の三角形が範囲に入るか。入れば左右のどちらか。
        internal bool TrySide(int targetTriangle, out bool left) => targetSide.TryGetValue(targetTriangle, out left);

        // §4.2・§4.3：転送先の三角形 targetTriangle の重心座標 barycentric の点から、転送元の UV を求める。
        // point・normal は、縁の帯で混ぜる「今の対応づけの点」を SourceGrid.Nearest で探すための位置と法線
        // （Build では画素の位置と TargetNormals、補正ピンでは動かした先の点と法線）。
        // 範囲の外の三角形、または転送元の円盤で見つからないときは false（呼び出し側は今の対応づけのままにする）。
        // 共有の書き込み状態は無く、Parallel.For から呼んでよい。
        internal bool TrySourceUv(FBXUVBodyCorrespondence map, int targetTriangle, Vector3 barycentric, Vector3 point, Vector3 normal,
            out Vector2 uv)
        {
            if (map == null) throw new ArgumentNullException(nameof(map));
            uv = Vector2.zero;
            if (!TrySide(targetTriangle, out var left)) return false;
            var target = left ? TargetLeft : TargetRight;
            var source = left ? SourceLeft : SourceRight;
            if (!FBXUVBodyRegionTransfer.TryMap(target, source, map.Target, map.Source, targetTriangle, barycentric,
                    out var sourceTriangle, out var sourceBarycentric, out var disc)) return false;

            var discUv = InterpolateUv(map.Source, sourceTriangle, sourceBarycentric);
            var radius = disc.magnitude;
            if (radius < FBXUVBodyRegionTransfer.BlendStart)
            {
                uv = discUv;
                return true;
            }

            var maxDistance = FBXUVBodyCorrespondence.MaxDistance * map.Target.SizeFactor;
            if (!map.SourceGrid.Nearest(point, normal, FBXUVBodyCorrespondence.MaxRing, maxDistance, out var regularTriangle,
                    out var regularBarycentric, out _))
            {
                uv = discUv;
                return true;
            }

            var discPoint = InterpolatePosition(map.AlignedSource, map.Source, sourceTriangle, sourceBarycentric);
            var regularPoint = InterpolatePosition(map.AlignedSource, map.Source, regularTriangle, regularBarycentric);
            var blended = Vector3.Lerp(regularPoint, discPoint, FBXUVBodyRegionTransfer.BlendWeight(radius));
            if (map.SourceGrid.Nearest(blended, normal, FBXUVBodyCorrespondence.MaxRing, maxDistance, out var blendedTriangle,
                    out var blendedBarycentric, out _))
            {
                uv = InterpolateUv(map.Source, blendedTriangle, blendedBarycentric);
            }
            else
            {
                uv = discUv;
            }

            return true;
        }

        private static Vector2 InterpolateUv(FBXUVBodyGeometry geometry, int triangle, Vector3 barycentric)
        {
            var t = geometry.Triangles;
            return geometry.Uvs[t[triangle * 3]] * barycentric.x + geometry.Uvs[t[triangle * 3 + 1]] * barycentric.y
                + geometry.Uvs[t[triangle * 3 + 2]] * barycentric.z;
        }

        private static Vector3 InterpolatePosition(Vector3[] positions, FBXUVBodyGeometry geometry, int triangle, Vector3 barycentric)
        {
            var t = geometry.Triangles;
            return positions[t[triangle * 3]] * barycentric.x + positions[t[triangle * 3 + 1]] * barycentric.y
                + positions[t[triangle * 3 + 2]] * barycentric.z;
        }
    }
}
