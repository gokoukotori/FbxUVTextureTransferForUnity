using System;
using System.Collections.Generic;
using UnityEngine;

namespace GokouKotori.FBXUVTextureTransfer
{
    // A small uniform grid limits the GPU containment test to nearby source triangles.
    // Both the mapped UV and each bilinear source texel use the same triangle test;
    // a raster mask alone cannot distinguish sub-texel points across a UV boundary.
    internal sealed class FBXUVSourceTriangleMask : IDisposable
    {
        private int gridSize;
        private ComputeBuffer triangles;
        private ComputeBuffer cellRanges;
        private ComputeBuffer cellIndices;

        // The records and grid the GPU buffers hold: two float4 per non-degenerate triangle, (a, b) and (c, 0, 0).
        // CPU references of FBXUVSourceSampling.hlsl read the same data.
        internal sealed class Lookup
        {
            internal int GridSize;
            internal List<Vector4> Triangles;
            internal Vector2Int[] CellRanges;
            internal int[] CellIndices;
        }

        internal static FBXUVSourceTriangleMask Create(IReadOnlyList<FBXUVTriangle> source, int gridSize,
            string emptyMessage, string invalidUvMessage, string degenerateMessage)
        {
            var lookup = BuildLookup(source, gridSize, emptyMessage, invalidUvMessage, degenerateMessage);
            var records = lookup.Triangles;
            var ranges = lookup.CellRanges;
            var indices = lookup.CellIndices;
            var mask = new FBXUVSourceTriangleMask { gridSize = gridSize };
            try
            {
                mask.triangles = new ComputeBuffer(records.Count, sizeof(float) * 4);
                mask.triangles.SetData(records);
                mask.cellRanges = new ComputeBuffer(ranges.Length, sizeof(int) * 2);
                mask.cellRanges.SetData(ranges);
                mask.cellIndices = new ComputeBuffer(indices.Length, sizeof(int));
                mask.cellIndices.SetData(indices);
                return mask;
            }
            catch { mask.Dispose(); throw; }
        }

        internal static Lookup BuildLookup(IReadOnlyList<FBXUVTriangle> source, int gridSize,
            string emptyMessage, string invalidUvMessage, string degenerateMessage)
        {
            if (gridSize <= 0) throw new ArgumentOutOfRangeException(nameof(gridSize));
            if (source == null || source.Count == 0) throw new ArgumentException(emptyMessage);
            var records = new List<Vector4>();
            var grid = new FBXUVTriangleGrid(gridSize);
            foreach (var triangle in source)
            {
                if (triangle == null || !FBXUVMakeupWarp.IsUnitUv(triangle.a)
                    || !FBXUVMakeupWarp.IsUnitUv(triangle.b) || !FBXUVMakeupWarp.IsUnitUv(triangle.c))
                    throw new ArgumentException(invalidUvMessage);
                var abX = (double)triangle.b.x - triangle.a.x;
                var abY = (double)triangle.b.y - triangle.a.y;
                var acX = (double)triangle.c.x - triangle.a.x;
                var acY = (double)triangle.c.y - triangle.a.y;
                var bcX = (double)triangle.c.x - triangle.b.x;
                var bcY = (double)triangle.c.y - triangle.b.y;
                var maxEdgeSquared = Math.Max(abX * abX + abY * abY,
                    Math.Max(acX * acX + acY * acY, bcX * bcX + bcY * bcY));
                if (Math.Abs(abX * acY - abY * acX) <= maxEdgeSquared * 1e-12) continue;
                var index = records.Count / 2;
                records.Add(new Vector4(triangle.a.x, triangle.a.y, triangle.b.x, triangle.b.y));
                records.Add(new Vector4(triangle.c.x, triangle.c.y, 0, 0));
                // Match the barycentric tolerance used on shared edges by the shader.
                // Expanding the bins avoids dropping a candidate at a grid-cell boundary.
                var minX = Cell(Mathf.Min(triangle.a.x, Mathf.Min(triangle.b.x, triangle.c.x)) - 2e-6f, gridSize);
                var maxX = Cell(Mathf.Max(triangle.a.x, Mathf.Max(triangle.b.x, triangle.c.x)) + 2e-6f, gridSize);
                var minY = Cell(Mathf.Min(triangle.a.y, Mathf.Min(triangle.b.y, triangle.c.y)) - 2e-6f, gridSize);
                var maxY = Cell(Mathf.Max(triangle.a.y, Mathf.Max(triangle.b.y, triangle.c.y)) + 2e-6f, gridSize);
                grid.Add(index, minX, maxX, minY, maxY);
            }
            if (records.Count == 0) throw new ArgumentException(degenerateMessage);
            grid.Build(out var ranges, out var indices);
            return new Lookup { GridSize = gridSize, Triangles = records, CellRanges = ranges, CellIndices = indices };
        }

        internal void Bind(Material material)
        {
            material.SetInt("_SourceGridSize", gridSize);
            material.SetBuffer("_SourceTriangles", triangles);
            material.SetBuffer("_SourceCellRanges", cellRanges);
            material.SetBuffer("_SourceCellIndices", cellIndices);
        }

        private static int Cell(float value, int gridSize) => Mathf.Clamp(Mathf.FloorToInt(value * gridSize), 0, gridSize - 1);

        public void Dispose()
        {
            cellIndices?.Release(); cellIndices = null;
            cellRanges?.Release(); cellRanges = null;
            triangles?.Release(); triangles = null;
        }
    }
}
