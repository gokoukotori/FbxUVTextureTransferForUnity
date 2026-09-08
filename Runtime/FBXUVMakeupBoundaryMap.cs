using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace GokouKotori.FBXUVTextureTransfer
{
    /// <summary>Automatic mouth-boundary map over a local annulus. Never edits saved controls.</summary>
    public static class FBXUVMakeupBoundaryMap
    {
        private const int MaximumBoundaryPoints = 512;
        private static double Cross(Vector2 a, Vector2 b) => a.x * (double)b.y - a.y * (double)b.x;
        private static double Area(IReadOnlyList<Vector2> p, int[] t) => Cross(p[t[1]] - p[t[0]], p[t[2]] - p[t[0]]);
        private static (int, int) Edge(int a, int b) => a < b ? (a, b) : (b, a);

        public static bool TryCreate(FBXUVMakeupTransferLayer layer, FBXUVMakeupExactMap baseline,
            out FBXUVMakeupExactMap map, out string reason)
        {
            map = null;
            reason = "口境界の自動補正は適用されていません。";
            if (layer == null || baseline == null) return false;
            var controls = layer.landmarks;
            // Only use the existing semantic mouth group; arbitrary manual point sets remain unchanged.
            if (controls == null || controls.Count < 24 || controls[0]?.name != "口：UV左口角"
                || controls[4]?.name != "口：UV右口角") return false;
            if (!SelectMouth(layer.sourceRegion, controls.Take(8).Select(p => p.sourceUv).ToArray(), out var source)
                || !SelectMouth(layer.targetRegion, controls.Take(8).Select(p => p.targetUv).ToArray(), out var target))
            { reason = "口の閉境界を一意に取得できないため、従来の対応点補間を使用します。"; return false; }
            Pair(source, target, out var innerSource, out var innerTarget, out var angles);
            if (innerSource.Count < 8 || innerSource.Count > MaximumBoundaryPoints) return false;
            if (innerTarget.All(p => baseline.TryEvaluate(p, out var q) && DistanceToBoundary(q, source) < 1e-6))
            { reason = "口境界は既に一致しているため、従来の対応点補間を使用します。"; return false; }
            var min = target.Aggregate(Vector2.Min); var max = target.Aggregate(Vector2.Max);
            var center = (min + max) * .5f; var half = (max - min) * .5f;
            if (half.x < 1e-5f) return false;
            var baseEdges = baseline.TargetEdges().ToArray();
            foreach (var vertical in new[] { 0f, -.2f, .2f })
                foreach (var width in new[] { 1.5f, 2.2f, 3f })
                    foreach (var shift in new[] { 0f, -.06f, .06f, -.12f, .12f })
                    {
                        var p = new List<Vector2>(innerTarget); var s = new List<Vector2>(innerSource);
                        var valid = true; var n = p.Count;
                        foreach (var angle in angles)
                        {
                            var outer = center + new Vector2((float)Math.Cos(angle + shift) * half.x * width,
                                vertical * half.x + (float)Math.Sin(angle + shift) * half.x * 1.3f);
                            if (!FBXUVMakeupWarp.IsUnitUv(outer) || !baseline.TryEvaluate(outer, out var uv)
                                || !FBXUVMakeupWarp.IsUnitUv(uv)) { valid = false; break; }
                            p.Add(outer); s.Add(uv);
                        }
                        if (!valid) continue;
                        var triangles = new List<int[]>();
                        for (var i = 0; i < n; i++)
                        {
                            var j = (i + 1) % n;
                            triangles.Add(new[] { i, j, j + n }); triangles.Add(new[] { i, j + n, i + n });
                        }
                        Repair(p, s, triangles);
                        if (!Positive(p, s, triangles)) continue;
                        if (!AlignSeam(p, s, triangles, n, baseline, baseEdges) || !Positive(p, s, triangles)) continue;
                        if (!SimpleBoundaries(p, triangles) || !SimpleBoundaries(s, triangles)) continue;
                        try
                        {
                            var candidate = FBXUVMakeupExactMap.FromTriangles(p, s, triangles);
                            // Other user landmarks must retain their meaning if they lie inside the patch.
                            if (controls.Skip(8).Any(c => candidate.TryEvaluate(c.targetUv, out var q)
                                && (!baseline.TryEvaluate(c.targetUv, out var expected)
                                    || Vector2.Distance(q, expected) > 1e-5f))) continue;
                            map = candidate;
                            reason = "口境界を自動補正: " + map.TriangleCount + "三角形。口周辺では保存点より実際のUV境界を優先します。";
                            return true;
                        }
                        catch (ArgumentException) { /* This candidate is unsafe; try the next bounded placement. */ }
                    }
            reason = "反転・重なりのない口境界写像を生成できないため、従来の対応点補間を使用します。";
            return false;
        }

        private static bool SelectMouth(FBXUVTransferRegion region, Vector2[] controls, out List<Vector2> mouth)
        {
            mouth = null;
            if (region?.triangles == null || controls.Length != 8) return false;
            var points = new List<Vector2>(); var ids = new Dictionary<(long, long), int>();
            var edges = new Dictionary<(int, int), int>();
            foreach (var triangle in region.triangles)
            {
                if (triangle == null) return false;
                var face = new int[3];
                for (var i = 0; i < 3; i++)
                {
                    var uv = triangle.GetPoint(i);
                    if (!FBXUVMakeupWarp.IsUnitUv(uv)) return false;
                    var key = ((long)Math.Round(uv.x * 1000000d), (long)Math.Round(uv.y * 1000000d));
                    if (!ids.TryGetValue(key, out var id))
                    { id = points.Count; ids.Add(key, id); points.Add(new Vector2(key.Item1 / 1000000f, key.Item2 / 1000000f)); }
                    face[i] = id;
                }
                if (face.Distinct().Count() != 3 || Math.Abs(Area(points, face)) <= 1e-14) continue;
                for (var i = 0; i < 3; i++)
                { var key = Edge(face[i], face[(i + 1) % 3]); edges.TryGetValue(key, out var count); edges[key] = count + 1; }
            }
            var adjacency = new Dictionary<int, List<int>>();
            foreach (var edge in edges.Where(e => e.Value == 1).Select(e => e.Key))
            {
                if (!adjacency.ContainsKey(edge.Item1)) adjacency.Add(edge.Item1, new List<int>());
                if (!adjacency.ContainsKey(edge.Item2)) adjacency.Add(edge.Item2, new List<int>());
                adjacency[edge.Item1].Add(edge.Item2); adjacency[edge.Item2].Add(edge.Item1);
            }
            var remaining = new HashSet<int>(adjacency.Keys); var candidates = new List<(double, List<Vector2>)>();
            while (remaining.Count > 0)
            {
                var first = remaining.Min(); var component = new HashSet<int> { first }; var queue = new Queue<int>(); queue.Enqueue(first);
                while (queue.Count > 0)
                    foreach (var next in adjacency[queue.Dequeue()]) if (component.Add(next)) queue.Enqueue(next);
                remaining.ExceptWith(component);
                if (component.Count > MaximumBoundaryPoints || component.Any(i => adjacency[i].Count != 2)) continue;
                var loop = new List<Vector2>(); var current = first; var previous = -1;
                do
                {
                    loop.Add(points[current]); var next = adjacency[current][0] == previous ? adjacency[current][1] : adjacency[current][0];
                    previous = current; current = next;
                } while (current != first && loop.Count <= component.Count);
                if (current != first || loop.Count < 3) continue;
                var score = controls.Average(c => DistanceToBoundary(c, loop));
                var span = loop.Max(v => v.x) - loop.Min(v => v.x);
                // Reject a distant or unrelated outer/eye boundary. Do not guess across missing loops.
                if (span <= 1e-5 || controls.Max(c => DistanceToBoundary(c, loop)) > span * .15) continue;
                candidates.Add((score, loop));
            }
            candidates.Sort((a, b) => a.Item1.CompareTo(b.Item1));
            if (candidates.Count == 0 || candidates.Count > 1 && candidates[1].Item1 <= candidates[0].Item1 + 1e-6) return false;
            mouth = candidates[0].Item2; return true;
        }

        private static double DistanceToBoundary(Vector2 point, List<Vector2> loop)
        {
            var best = double.PositiveInfinity;
            for (var i = 0; i < loop.Count; i++)
            {
                var a = loop[i]; var d = loop[(i + 1) % loop.Count] - a;
                var t = Mathf.Clamp01(Vector2.Dot(point - a, d) / d.sqrMagnitude);
                best = Math.Min(best, Vector2.Distance(point, a + t * d));
            }
            return best;
        }

        private static List<Vector2>[] Arcs(List<Vector2> loop)
        {
            var left = 0; var right = 0;
            for (var i = 1; i < loop.Count; i++)
            { if (loop[i].x < loop[left].x) left = i; if (loop[i].x > loop[right].x) right = i; }
            var paths = new List<Vector2>[2];
            for (var k = 0; k < 2; k++)
            {
                var path = new List<Vector2> { loop[left] }; var i = left;
                while (i != right) { i = (i + (k == 0 ? 1 : -1) + loop.Count) % loop.Count; path.Add(loop[i]); }
                paths[k] = path;
            }
            if (paths[0].Average(v => v.y) < paths[1].Average(v => v.y)) Array.Reverse(paths);
            return paths;
        }

        private static double[] Knots(List<Vector2> path)
        {
            var result = new double[path.Count];
            for (var i = 1; i < path.Count; i++) result[i] = result[i - 1] + Vector2.Distance(path[i - 1], path[i]);
            var length = result[result.Length - 1];
            for (var i = 1; i < result.Length; i++) result[i] /= length;
            return result;
        }

        private static Vector2 Sample(List<Vector2> path, double[] knots, double t)
        {
            for (var i = 1; i < path.Count; i++)
                if (t <= knots[i]) return Vector2.LerpUnclamped(path[i - 1], path[i], (float)((t - knots[i - 1]) / (knots[i] - knots[i - 1])));
            return path[path.Count - 1];
        }

        private static void Pair(List<Vector2> source, List<Vector2> target,
            out List<Vector2> s, out List<Vector2> p, out List<double> angles)
        {
            s = new List<Vector2>(); p = new List<Vector2>(); angles = new List<double>();
            var sa = Arcs(source); var ta = Arcs(target);
            for (var arc = 0; arc < 2; arc++)
            {
                var sk = Knots(sa[arc]); var tk = Knots(ta[arc]);
                var all = sk.Concat(tk).Concat(Enumerable.Range(0, 33).Select(i => i / 32d)).OrderBy(t => t);
                var knots = new List<double>();
                foreach (var t in all) if (knots.Count == 0 || t - knots[knots.Count - 1] > 1e-6) knots.Add(t);
                knots[knots.Count - 1] = 1;
                if (arc == 0) knots.RemoveAt(knots.Count - 1);
                else { knots.RemoveAt(0); knots.Reverse(); }
                foreach (var t in knots)
                {
                    var sp = Sample(sa[arc], sk, t); var tp = Sample(ta[arc], tk, t);
                    // GPU float coordinates cannot represent arbitrarily close union knots.
                    if (p.Count > 0 && ((p[p.Count - 1] - tp).sqrMagnitude < 1e-14f || (s[s.Count - 1] - sp).sqrMagnitude < 1e-14f)) continue;
                    s.Add(sp); p.Add(tp); angles.Add(Math.PI * (arc == 0 ? 1 - t : 1 + t));
                }
            }
            if (p.Count > 1 && ((p[0] - p[p.Count - 1]).sqrMagnitude < 1e-14f || (s[0] - s[s.Count - 1]).sqrMagnitude < 1e-14f))
            { p.RemoveAt(p.Count - 1); s.RemoveAt(s.Count - 1); angles.RemoveAt(angles.Count - 1); }
        }

        private static bool Positive(List<Vector2> p, List<Vector2> s, List<int[]> triangles)
            => triangles.All(t => Area(p, t) > 1e-14 && Area(s, t) > 1e-14);

        private static void Repair(List<Vector2> p, List<Vector2> s, List<int[]> triangles)
        {
            for (var pass = 0; pass < triangles.Count; pass++)
            {
                var changed = false;
                for (var i = 0; i < triangles.Count && !changed; i++)
                {
                    var a = triangles[i]; if (Area(p, a) > 1e-14 && Area(s, a) > 1e-14) continue;
                    for (var j = 0; j < triangles.Count; j++)
                    {
                        var b = triangles[j]; var shared = a.Intersect(b).ToArray(); if (shared.Length != 2) continue;
                        var x = a.First(v => !b.Contains(v)); var y = b.First(v => !a.Contains(v));
                        var c = new[] { x, y, shared[0] }; var d = new[] { y, x, shared[1] };
                        if (Area(p, c) < 0) { Array.Reverse(c); Array.Reverse(d); }
                        if (Area(p, c) <= 1e-14 || Area(p, d) <= 1e-14 || Area(s, c) <= 1e-14 || Area(s, d) <= 1e-14) continue;
                        triangles[i] = c; triangles[j] = d; changed = true; break;
                    }
                }
                if (!changed) return;
            }
        }

        private static bool AlignSeam(List<Vector2> p, List<Vector2> s, List<int[]> triangles, int n,
            FBXUVMakeupExactMap baseline, Vector2[][] baseEdges)
        {
            for (var i = 0; i < n; i++)
            {
                var a = n + i; var b = n + (i + 1) % n; var u = p[a]; var d = p[b] - u;
                var knots = new List<double>();
                foreach (var edge in baseEdges)
                {
                    var v = edge[0]; var f = edge[1] - v; var den = Cross(d, f);
                    if (Math.Abs(den) < 1e-14) continue;
                    var t = Cross(v - u, f) / den; var r = Cross(v - u, d) / den;
                    if (t > 1e-5 && t < 1 - 1e-5 && r >= -1e-7 && r <= 1 + 1e-7) knots.Add(t);
                }
                knots.Sort(); var previous = a; var last = -1d;
                foreach (var t in knots)
                {
                    if (t - last < 1e-5) continue; last = t;
                    var index = triangles.FindIndex(face => face.Contains(previous) && face.Contains(b));
                    if (index < 0) return false;
                    var point = u + (float)t * d;
                    if ((point - p[previous]).sqrMagnitude < 1e-14f || (point - p[b]).sqrMagnitude < 1e-14f) continue;
                    if (!baseline.TryEvaluate(point, out var uv)) return false;
                    var c = triangles[index].First(v => v != previous && v != b); triangles.RemoveAt(index);
                    var id = p.Count; p.Add(point); s.Add(uv);
                    foreach (var face in new[] { new[] { previous, id, c }, new[] { id, b, c } })
                    { if (Area(p, face) < 0) Array.Reverse(face); triangles.Add(face); }
                    previous = id;
                }
            }
            return true;
        }

        private static bool SimpleBoundaries(List<Vector2> points, List<int[]> faces)
        {
            var counts = new Dictionary<(int, int), int>();
            foreach (var t in faces)
                for (var i = 0; i < 3; i++) { var e = Edge(t[i], t[(i + 1) % 3]); counts.TryGetValue(e, out var n); counts[e] = n + 1; }
            if (counts.Values.Any(n => n > 2)) return false;
            var edges = counts.Where(e => e.Value == 1).Select(e => e.Key).ToArray();
            for (var i = 0; i < edges.Length; i++)
                for (var j = 0; j < i; j++)
                {
                    var a = edges[i]; var b = edges[j];
                    if (a.Item1 == b.Item1 || a.Item1 == b.Item2 || a.Item2 == b.Item1 || a.Item2 == b.Item2) continue;
                    var u = points[a.Item1]; var v = points[a.Item2]; var w = points[b.Item1]; var z = points[b.Item2];
                    if (Cross(v - u, w - u) * Cross(v - u, z - u) < -1e-22
                        && Cross(z - w, u - w) * Cross(z - w, v - w) < -1e-22) return false;
                }
            return true;
        }
    }
}
