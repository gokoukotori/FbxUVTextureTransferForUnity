using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace GokouKotori.FBXUVTextureTransfer
{
    // 位置が同じ頂点（UV シームで分かれた頂点）を束ね、束どうしの隣接を作る。
    internal static class FBXUVBodyWeld
    {
        private const float WeldScale = 10000f;

        // Vertices closer than 0.1 mm (scaled by the geometry's size factor) share one group.
        internal static int[] Groups(FBXUVBodyGeometry geometry, out int groupCount)
        {
            var positions = geometry.Positions;
            var sizeFactor = geometry.SizeFactor;
            var scale = sizeFactor > 0f ? WeldScale / sizeFactor : WeldScale;
            var weldOf = new int[positions.Length];
            var keys = new Dictionary<(int, int, int), int>();
            for (var v = 0; v < positions.Length; v++)
            {
                var p = positions[v] * scale;
                var key = (Mathf.RoundToInt(p.x), Mathf.RoundToInt(p.y), Mathf.RoundToInt(p.z));
                if (!keys.TryGetValue(key, out var id)) keys[key] = id = keys.Count;
                weldOf[v] = id;
            }

            groupCount = keys.Count;
            return weldOf;
        }

        // Neighbouring groups of each group, without itself and without duplicates.
        internal static int[][] Adjacency(int[] triangles, int[] weldOf, int groupCount)
        {
            var neighbours = new HashSet<int>[groupCount];
            for (var w = 0; w < groupCount; w++) neighbours[w] = new HashSet<int>();
            for (var f = 0; f < triangles.Length / 3; f++)
            {
                int wa = weldOf[triangles[f * 3]], wb = weldOf[triangles[f * 3 + 1]], wc = weldOf[triangles[f * 3 + 2]];
                neighbours[wa].Add(wb); neighbours[wa].Add(wc);
                neighbours[wb].Add(wa); neighbours[wb].Add(wc);
                neighbours[wc].Add(wa); neighbours[wc].Add(wb);
            }
            // A triangle collapsed by welding would put a vertex in its own neighbour set.
            for (var w = 0; w < groupCount; w++) neighbours[w].Remove(w);
            return neighbours.Select(h => h.ToArray()).ToArray();
        }
    }
}
