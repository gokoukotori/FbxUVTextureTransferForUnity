using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace GokouKotori.FBXUVTextureTransfer.Editor
{
    internal static class FBXUVMakeupLandmarkUtility
    {
        internal sealed class BoundaryAnalysis
        {
            internal readonly List<List<Vector2>> ClosedLoops = new List<List<Vector2>>();
            internal FBXUVBounds Bounds;
            internal int IgnoredDegenerateTriangles;
            internal int OpenVertexCount;
            internal int NonManifoldEdgeCount;
            internal string Diagnostics => "閉境界 " + ClosedLoops.Count + " / 退化三角形の除外 " + IgnoredDegenerateTriangles
                + " / 開境界・分岐頂点 " + OpenVertexCount + " / 非多様体辺 " + NonManifoldEdgeCount;
        }

        internal struct BoundarySelection
        {
            internal int mouth, eyeLeftUv, eyeRightUv;
            internal static BoundarySelection Empty => new BoundarySelection { mouth = -1, eyeLeftUv = -1, eyeRightUv = -1 };
        }

        internal static bool TryAnalyze(FBXUVTransferRegion region, out BoundaryAnalysis analysis, out string error)
        {
            analysis = null;
            if (!FBXUVBoundaryUtility.TryAnalyze(region?.triangles, false, int.MaxValue, out var result, out error)) return false;
            analysis = new BoundaryAnalysis
            {
                Bounds = FBXUVBounds.FromTriangles(region.triangles),
                IgnoredDegenerateTriangles = result.IgnoredDegenerateTriangles,
                OpenVertexCount = result.OpenVertexCount,
                NonManifoldEdgeCount = result.NonManifoldEdgeCount
            };
            analysis.ClosedLoops.AddRange(result.ClosedLoops);
            return true;
        }

        internal static bool TrySuggestLoops(BoundaryAnalysis analysis, out BoundarySelection selection, out string error)
        {
            selection = BoundarySelection.Empty;
            error = "目・口の境界を一意に提案できません。境界を個別に選択するか、手動の目印を配置してください。";
            if (analysis == null || !analysis.Bounds.IsValid) return false;
            var frame = analysis.Bounds;
            var area = frame.Width * frame.Height;
            var candidates = Enumerable.Range(0, analysis.ClosedLoops.Count)
                .Where(i => Math.Abs(SignedArea(analysis.ClosedLoops[i])) > Math.Max(1e-6f, area * .00002f)
                    && Math.Abs(SignedArea(analysis.ClosedLoops[i])) < area * .2f).ToArray();
            // A complete outer boundary plus exactly three inner candidates also supports
            // slit-shaped eye openings. With extra holes, keep the stricter shape filter
            // so narrow eyebrow-like boundaries do not replace the eyes.
            var maximumEyeAspect = analysis.ClosedLoops.Count == 4 && candidates.Length == 3
                && analysis.OpenVertexCount == 0 && analysis.NonManifoldEdgeCount == 0 ? 16f : 8f;
            var suggestions = new List<(BoundarySelection selection, float height, float eyeHeight)>();
            foreach (var left in candidates)
            foreach (var right in candidates)
            {
                var a = LoopBounds(analysis.ClosedLoops[left]); var b = LoopBounds(analysis.ClosedLoops[right]);
                if (a.Center.x >= frame.Center.x || b.Center.x <= frame.Center.x) continue;
                if (a.Width < a.Height * .5f || b.Width < b.Height * .5f
                    || a.Width > a.Height * maximumEyeAspect || b.Width > b.Height * maximumEyeAspect
                    || a.Width > frame.Width * .35f || b.Width > frame.Width * .35f
                    || a.Height > frame.Height * .3f || b.Height > frame.Height * .3f) continue;
                if (Mathf.Abs(a.Center.y - b.Center.y) > Mathf.Max(a.Height, b.Height) * .6f
                    || a.Width / b.Width < .4f || a.Width / b.Width > 2.5f
                    || a.Height / b.Height < .4f || a.Height / b.Height > 2.5f) continue;
                var leftDistance = frame.Center.x - a.Center.x; var rightDistance = b.Center.x - frame.Center.x;
                if (leftDistance / rightDistance < .5f || leftDistance / rightDistance > 2f) continue;
                var mouths = candidates.Where(i => i != left && i != right).Where(i => {
                    var m = LoopBounds(analysis.ClosedLoops[i]);
                    return m.Center.x > a.Center.x && m.Center.x < b.Center.x
                        && Mathf.Abs(m.Center.x - frame.Center.x) < (b.Center.x - a.Center.x) * .25f
                        && m.maxV < Mathf.Min(a.minV, b.minV)
                        && m.Width >= m.Height && m.Width < b.Center.x - a.Center.x
                        && m.Height < frame.Height * .15f;
                }).ToArray();
                if (mouths.Length != 1) continue;
                suggestions.Add((new BoundarySelection { mouth = mouths[0], eyeLeftUv = left, eyeRightUv = right },
                    (a.Center.y + b.Center.y) * .5f, Mathf.Max(a.Height, b.Height)));
            }
            if (suggestions.Count == 0) return false;
            suggestions = suggestions.OrderByDescending(s => s.height).ToList();
            if (suggestions.Count > 1 && suggestions[0].height - suggestions[1].height <= suggestions[0].eyeHeight * .5f) return false;
            selection = suggestions[0].selection;
            error = null;
            return true;
        }

        internal static bool TryGenerateFromLoops(BoundaryAnalysis source, BoundarySelection sourceSelection,
            BoundaryAnalysis target, BoundarySelection targetSelection, out List<FBXUVMakeupLandmark> landmarks, out string error)
        {
            landmarks = null;
            if (!TryControls(source, sourceSelection, out var a, out error)) { error = "Source: " + error; return false; }
            if (!TryControls(target, targetSelection, out var b, out error)) { error = "Target: " + error; return false; }
            landmarks = PairControls(a, b);
            error = null;
            return true;
        }

        private static bool TryControls(BoundaryAnalysis analysis, BoundarySelection selection, out List<Vector2> points, out string error)
        {
            points = null;
            error = "口・UV左眼・UV右眼に異なる閉境界を指定してください。";
            var indices = new[] { selection.mouth, selection.eyeLeftUv, selection.eyeRightUv };
            if (analysis == null || indices.Distinct().Count() != 3 || indices.Any(i => i < 0 || i >= analysis.ClosedLoops.Count)) return false;
            var result = new List<Vector2>();
            foreach (var index in indices)
                if (Math.Abs(SignedArea(analysis.ClosedLoops[index])) <= 1e-10f || !AddLoopControls(analysis.ClosedLoops[index], result)) return false;
            result.AddRange(new[] { Vector2.zero, Vector2.right, Vector2.up, Vector2.one });
            for (var i = 0; i < result.Count; i++)
                for (var j = 0; j < i; j++)
                    if ((result[i] - result[j]).sqrMagnitude <= 1e-12f)
                    { error = "生成した対応点が重複・近接しています。別の境界を選ぶか、手動で点を指定してください。"; return false; }
            points = result;
            error = null;
            return true;
        }

        internal static List<FBXUVMakeupLandmark> CreateManualTemplate(FBXUVTransferRegion source, FBXUVTransferRegion target)
        {
            return PairControls(TemplatePoints(source), TemplatePoints(target));
        }

        private static List<Vector2> TemplatePoints(FBXUVTransferRegion region)
        {
            var bounds = region != null && region.bounds.IsValid ? region.bounds : new FBXUVBounds(0, 0, 1, 1);
            var points = new List<Vector2>();
            foreach (var part in new[] { new Vector2(.5f, .3f), new Vector2(.35f, .6f), new Vector2(.65f, .6f) })
            {
                var width = part.y < .5f ? .1f : .09f;
                var height = part.y < .5f ? .025f : .045f;
                foreach (var unit in new[] { new Vector2(-1, 0), new Vector2(-.5f, .8f), Vector2.up,
                    new Vector2(.5f, .8f), Vector2.right, new Vector2(-.5f, -.8f), Vector2.down, new Vector2(.5f, -.8f) })
                    points.Add(new Vector2(bounds.minU + (part.x + width * unit.x) * bounds.Width,
                        bounds.minV + (part.y + height * unit.y) * bounds.Height));
            }
            points.AddRange(new[] { Vector2.zero, Vector2.right, Vector2.up, Vector2.one });
            return points;
        }

        private static List<FBXUVMakeupLandmark> PairControls(List<Vector2> a, List<Vector2> b)
        {
            var result = new List<FBXUVMakeupLandmark>();
            for (var i = 0; i < a.Count; i++) result.Add(new FBXUVMakeupLandmark { name = PointName(i), sourceUv = a[i], targetUv = b[i] });
            return result;
        }

        internal static string PointName(int index)
        {
            if (index >= 24) return "画像隅 " + (index - 23);
            var part = index / 8; var point = index % 8;
            if (part == 0) return new[] { "口：UV左口角", "上唇：UV左", "上唇：中央", "上唇：UV右", "口：UV右口角", "下唇：UV左", "下唇：中央", "下唇：UV右" }[point];
            var side = part == 1 ? "UV左眼：" : "UV右眼：";
            if (point == 0) return side + (part == 1 ? "目尻" : "目頭");
            if (point == 4) return side + (part == 1 ? "目頭" : "目尻");
            return side + (point < 4 ? "上まぶた " + point : "下まぶた " + (point - 4));
        }

        internal static FBXUVBounds LoopBounds(List<Vector2> loop)
        {
            return new FBXUVBounds(loop.Min(p => p.x), loop.Min(p => p.y), loop.Max(p => p.x), loop.Max(p => p.y));
        }

        private static bool AddLoopControls(List<Vector2> loop, List<Vector2> output)
        {
            var left = 0; var right = 0;
            for (var i = 1; i < loop.Count; i++)
            {
                if (loop[i].x < loop[left].x) left = i;
                if (loop[i].x > loop[right].x) right = i;
            }
            if (left == right) return false;
            var a = Arc(loop, left, right, 1);
            var b = Arc(loop, left, right, -1);
            var upper = a.Average(p => p.y) > b.Average(p => p.y) ? a : b;
            var lower = ReferenceEquals(upper, a) ? b : a;
            for (var i = 0; i <= 4; i++) output.Add(Sample(upper, i / 4f));
            for (var i = 1; i < 4; i++) output.Add(Sample(lower, i / 4f));
            return true;
        }

        private static List<Vector2> Arc(List<Vector2> loop, int start, int end, int step)
        {
            var arc = new List<Vector2> { loop[start] };
            for (var i = (start + step + loop.Count) % loop.Count; ; i = (i + step + loop.Count) % loop.Count)
            {
                arc.Add(loop[i]);
                if (i == end) return arc;
            }
        }

        private static Vector2 Sample(List<Vector2> arc, float t)
        {
            var length = 0f;
            for (var i = 1; i < arc.Count; i++) length += Vector2.Distance(arc[i - 1], arc[i]);
            var distance = t * length;
            for (var i = 1; i < arc.Count; i++)
            {
                var segment = Vector2.Distance(arc[i - 1], arc[i]);
                if (distance <= segment && segment > 0) return Vector2.Lerp(arc[i - 1], arc[i], distance / segment);
                distance -= segment;
            }
            return arc[arc.Count - 1];
        }

        private static float SignedArea(List<Vector2> loop)
        {
            var area = 0f;
            for (var i = 0; i < loop.Count; i++)
            {
                var a = loop[i]; var b = loop[(i + 1) % loop.Count];
                area += a.x * b.y - b.x * a.y;
            }
            return area * .5f;
        }

    }
}
