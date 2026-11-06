using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace GokouKotori.FBXUVTextureTransfer
{
    internal static class FBXUVBoundaryUtility
    {
        internal sealed class Analysis
        {
            internal readonly List<List<Vector2>> ClosedLoops = new List<List<Vector2>>();
            internal int IgnoredDegenerateTriangles, OpenVertexCount, NonManifoldEdgeCount;
        }

        // Landmark diagnostics use original UV area; the mouth map uses quantized UV area.
        // Keep that policy explicit rather than changing which tiny faces each caller accepts.
        internal static bool TryAnalyze(IReadOnlyList<FBXUVTriangle> triangles, bool useQuantizedArea,
            int maximumBoundaryPoints, out Analysis analysis, out string error)
        {
            analysis = null;
            error = "顔のUV島を選択してください。";
            if (triangles == null || triangles.Count == 0) return false;
            var result = new Analysis();
            var vertices = new List<Vector2>();
            var vertexIds = new Dictionary<(long, long), int>();
            var edges = new Dictionary<(int, int), int>();
            foreach (var triangle in triangles)
            {
                if (triangle == null) { error = "UV三角形がありません。"; return false; }
                var ids = new int[3];
                for (var i = 0; i < 3; i++)
                {
                    var point = triangle.GetPoint(i);
                    if (!FBXUVMakeupWarp.IsUnitUv(point))
                    { error = "UVは有限な0～1の値にしてください。"; return false; }
                    var key = ((long)Math.Round(point.x * 1000000d), (long)Math.Round(point.y * 1000000d));
                    if (!vertexIds.TryGetValue(key, out var id))
                    {
                        id = vertices.Count;
                        vertexIds.Add(key, id);
                        // Preserve both original reconstruction expressions, including their precision.
                        vertices.Add(useQuantizedArea
                            ? new Vector2(key.Item1 / 1000000f, key.Item2 / 1000000f)
                            : new Vector2((float)(key.Item1 / 1000000d), (float)(key.Item2 / 1000000d)));
                    }
                    ids[i] = id;
                }
                var ab = useQuantizedArea ? vertices[ids[1]] - vertices[ids[0]] : triangle.b - triangle.a;
                var ac = useQuantizedArea ? vertices[ids[2]] - vertices[ids[0]] : triangle.c - triangle.a;
                if (ids.Distinct().Count() != 3 || Math.Abs(ab.x * (double)ac.y - ab.y * (double)ac.x) <= 1e-14)
                { result.IgnoredDegenerateTriangles++; continue; }
                for (var i = 0; i < 3; i++)
                {
                    var a = ids[i]; var b = ids[(i + 1) % 3];
                    var edge = a < b ? (a, b) : (b, a);
                    edges.TryGetValue(edge, out var count); edges[edge] = count + 1;
                }
            }
            result.NonManifoldEdgeCount = edges.Count(e => e.Value > 2);
            var adjacency = new Dictionary<int, List<int>>();
            foreach (var edge in edges.Where(e => e.Value == 1).Select(e => e.Key))
            {
                if (!adjacency.ContainsKey(edge.Item1)) adjacency.Add(edge.Item1, new List<int>());
                if (!adjacency.ContainsKey(edge.Item2)) adjacency.Add(edge.Item2, new List<int>());
                adjacency[edge.Item1].Add(edge.Item2); adjacency[edge.Item2].Add(edge.Item1);
            }
            result.OpenVertexCount = adjacency.Count(v => v.Value.Count != 2);
            var remaining = new HashSet<int>(adjacency.Keys);
            while (remaining.Count > 0)
            {
                var first = remaining.Min();
                var component = new HashSet<int> { first };
                var queue = new Queue<int>(); queue.Enqueue(first);
                while (queue.Count > 0)
                    foreach (var next in adjacency[queue.Dequeue()]) if (component.Add(next)) queue.Enqueue(next);
                remaining.ExceptWith(component);
                if (component.Count > maximumBoundaryPoints || component.Any(i => adjacency[i].Count != 2)) continue;
                var loop = new List<Vector2>(); var current = first; var previous = -1;
                do
                {
                    loop.Add(vertices[current]);
                    var next = adjacency[current][0] == previous ? adjacency[current][1] : adjacency[current][0];
                    previous = current; current = next;
                } while (current != first && loop.Count <= component.Count);
                if (current == first && loop.Count == component.Count && loop.Count >= 3) result.ClosedLoops.Add(loop);
            }
            analysis = result;
            error = null;
            return true;
        }
    }
}
