using System.Collections.Generic;
using UnityEngine;

namespace GokouKotori.FBXUVTextureTransfer
{
    // Builds only the lookup. Coordinate normalization, tolerances and GPU records belong to callers.
    internal sealed class FBXUVTriangleGrid
    {
        private readonly int size;
        private readonly List<int>[] cells;

        internal FBXUVTriangleGrid(int size)
        {
            this.size = size;
            cells = new List<int>[size * size];
        }

        internal void Add(int triangle, int minX, int maxX, int minY, int maxY)
        {
            for (var y = minY; y <= maxY; y++)
                for (var x = minX; x <= maxX; x++)
                {
                    var cell = y * size + x;
                    (cells[cell] ??= new List<int>()).Add(triangle);
                }
        }

        internal void Build(out Vector2Int[] ranges, out int[] indices)
        {
            ranges = new Vector2Int[cells.Length];
            var ids = new List<int>();
            for (var i = 0; i < cells.Length; i++)
            {
                ranges[i] = new Vector2Int(ids.Count, cells[i]?.Count ?? 0);
                if (cells[i] != null) ids.AddRange(cells[i]);
            }
            indices = ids.ToArray();
        }
    }
}
