using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace GokouKotori.FBXUVTextureTransfer
{
    public static class FBXUVMakeupTransferRenderer
    {
        public static void Render(FBXUVMakeupTransferLayer layer, RenderTexture destination)
        {
            if (layer == null) throw new ArgumentNullException(nameof(layer));
            Render(layer, destination, layer.GetMapping());
        }

        internal static void Render(FBXUVMakeupTransferLayer layer, RenderTexture destination,
            FBXUVMakeupMapping mapping, bool reuseResources = true)
        {
            if (layer == null) throw new ArgumentNullException(nameof(layer));
            if (destination == null) throw new ArgumentNullException(nameof(destination));
            if (mapping == null) throw new ArgumentNullException(nameof(mapping));
            var warp = mapping.Warp;
            if (layer.makeupTexture == null || layer.targetRegion == null || layer.sourceRegion == null)
                throw new ArgumentException("メイクTextureと転送元・転送先Regionが必要です。");
            var shader = layer.ResolveTransferShader();
            if (shader == null || !shader.isSupported) throw new InvalidOperationException("メイク転送Shaderを利用できません。");
            RenderResources resources = null;
            var retainResources = reuseResources && layer.IsEnabledInHierarchy;
            var previous = RenderTexture.active;
            var previousSrgb = GL.sRGBWrite;
            try
            {
                resources = retainResources ? layer.GetRenderResources(mapping, shader)
                    : new RenderResources(layer, mapping, shader, false);
                var material = resources.Material;
                material.SetTexture("_MainTex", layer.makeupTexture);
                material.SetInt("_PointCount", warp.Count);
                material.SetVectorArray("_Points", resources.GpuPoints);
                material.SetVector("_Affine0", warp.GetGpuAffine(0));
                material.SetVector("_AffineU", warp.GetGpuAffine(1));
                material.SetVector("_AffineV", warp.GetGpuAffine(2));
                material.SetVector("_SourceSize", new Vector4(layer.makeupTexture.width, layer.makeupTexture.height, 0, 0));
                var bounds = layer.sourceRegion.bounds;
                material.SetVector("_SourceBounds", new Vector4(bounds.minU, bounds.minV, bounds.maxU, bounds.maxV));
                material.SetFloat("_RestoreSRGB", GraphicsFormatUtility.IsSRGBFormat(layer.makeupTexture.graphicsFormat) ? 1f : 0f);
                Graphics.SetRenderTarget(destination);
                // TTT expects raw straight RGBA values, regardless of the project's working color space.
                GL.sRGBWrite = false;
                GL.Clear(false, true, Color.clear);
                if (!material.SetPass(0)) throw new InvalidOperationException("メイク転送Shader passを利用できません。");
                Graphics.DrawMeshNow(resources.Mesh, Matrix4x4.identity);
            }
            finally
            {
                GL.sRGBWrite = previousSrgb;
                Graphics.SetRenderTarget(previous);
                if (!retainResources) resources?.Dispose();
            }
        }

        private static readonly List<RenderResources> liveResources = new List<RenderResources>();

        internal static void ReleaseUnusedResources()
        {
            for (var i = liveResources.Count - 1; i >= 0; i--)
            {
                var resources = liveResources[i];
                if (resources.Owner == null || !resources.Owner.IsEnabledInHierarchy) resources.Dispose();
            }
        }

        internal static void ReleaseAllResources()
        {
            for (var i = liveResources.Count - 1; i >= 0; i--) liveResources[i].Dispose();
        }

        internal sealed class RenderResources : IDisposable
        {
            internal readonly FBXUVMakeupTransferLayer Owner;
            internal Material Material { get; private set; }
            internal Mesh Mesh { get; private set; }
            internal Vector4[] GpuPoints { get; private set; }
            private FBXUVMakeupMapping mapping;
            private FBXUVTriangle[] sourceTriangles, targetTriangles;
            private SourceTriangleMask sourceMask;
            private IDisposable exactBuffers, mouthBuffers;

            internal RenderResources(FBXUVMakeupTransferLayer layer, FBXUVMakeupMapping mapping,
                Shader shader, bool retain = true)
            {
                Owner = layer;
                this.mapping = mapping;
                try
                {
                    Material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
                    if (mapping.Exact != null)
                    {
                        exactBuffers = mapping.Exact.Bind(Material);
                        if (mapping.Mouth != null) mouthBuffers = mapping.Mouth.Bind(Material, true);
                    }
                    else Material.SetInt("_UseExactMapping", 0);
                    sourceMask = SourceTriangleMask.Create(layer.sourceRegion.triangles);
                    sourceMask.Bind(Material);
                    Mesh = CreateTargetMesh(layer.targetRegion.triangles);
                    GpuPoints = mapping.Warp.GetGpuPoints();
                    if (retain)
                    {
                        sourceTriangles = FBXUVMakeupComputationCache.CopyTriangles(layer.sourceRegion.triangles);
                        targetTriangles = FBXUVMakeupComputationCache.CopyTriangles(layer.targetRegion.triangles);
                        liveResources.Add(this);
                    }
                }
                catch { Dispose(); throw; }
            }

            internal bool Matches(FBXUVMakeupTransferLayer layer, FBXUVMakeupMapping current, Shader shader)
            {
                return Material != null && Mesh != null && Material.shader == shader && ReferenceEquals(mapping, current)
                    && FBXUVMakeupComputationCache.SameTriangles(sourceTriangles, layer.sourceRegion.triangles)
                    && FBXUVMakeupComputationCache.SameTriangles(targetTriangles, layer.targetRegion.triangles);
            }

            public void Dispose()
            {
                liveResources.Remove(this);
                sourceMask?.Dispose(); sourceMask = null;
                exactBuffers?.Dispose(); exactBuffers = null;
                mouthBuffers?.Dispose(); mouthBuffers = null;
                if (Mesh != null) UnityEngine.Object.DestroyImmediate(Mesh);
                if (Material != null) UnityEngine.Object.DestroyImmediate(Material);
                Mesh = null; Material = null; GpuPoints = null;
                mapping = null; sourceTriangles = targetTriangles = null;
            }
        }

        // A small uniform grid limits the GPU containment test to nearby source triangles.
        // Both the mapped UV and each bilinear source texel use the same triangle test;
        // a raster mask alone cannot distinguish sub-texel points across a UV boundary.
        private sealed class SourceTriangleMask : IDisposable
        {
            private const int GridSize = 32;
            private ComputeBuffer triangles;
            private ComputeBuffer cellRanges;
            private ComputeBuffer cellIndices;

            internal static SourceTriangleMask Create(IReadOnlyList<FBXUVTriangle> source)
            {
                if (source == null || source.Count == 0) throw new ArgumentException("転送元の顔三角形がありません。");
                var records = new List<Vector4>();
                var cells = new List<int>[GridSize * GridSize];
                foreach (var triangle in source)
                {
                    if (triangle == null || !FBXUVMakeupWarp.IsUnitUv(triangle.a)
                        || !FBXUVMakeupWarp.IsUnitUv(triangle.b) || !FBXUVMakeupWarp.IsUnitUv(triangle.c))
                        throw new ArgumentException("転送元の顔三角形には0〜1の有限UVが必要です。");
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
                    var minX = Cell(Mathf.Min(triangle.a.x, Mathf.Min(triangle.b.x, triangle.c.x)) - 2e-6f);
                    var maxX = Cell(Mathf.Max(triangle.a.x, Mathf.Max(triangle.b.x, triangle.c.x)) + 2e-6f);
                    var minY = Cell(Mathf.Min(triangle.a.y, Mathf.Min(triangle.b.y, triangle.c.y)) - 2e-6f);
                    var maxY = Cell(Mathf.Max(triangle.a.y, Mathf.Max(triangle.b.y, triangle.c.y)) + 2e-6f);
                    for (var y = minY; y <= maxY; y++)
                        for (var x = minX; x <= maxX; x++)
                        {
                            var cell = y * GridSize + x;
                            if (cells[cell] == null) cells[cell] = new List<int>();
                            cells[cell].Add(index);
                        }
                }
                if (records.Count == 0) throw new ArgumentException("転送元の顔三角形が全て退化しています。");
                var ranges = new Vector2Int[cells.Length];
                var indices = new List<int>();
                for (var i = 0; i < cells.Length; i++)
                {
                    ranges[i] = new Vector2Int(indices.Count, cells[i]?.Count ?? 0);
                    if (cells[i] != null) indices.AddRange(cells[i]);
                }
                var mask = new SourceTriangleMask();
                try
                {
                    mask.triangles = new ComputeBuffer(records.Count, sizeof(float) * 4);
                    mask.triangles.SetData(records);
                    mask.cellRanges = new ComputeBuffer(ranges.Length, sizeof(int) * 2);
                    mask.cellRanges.SetData(ranges);
                    mask.cellIndices = new ComputeBuffer(indices.Count, sizeof(int));
                    mask.cellIndices.SetData(indices);
                    return mask;
                }
                catch { mask.Dispose(); throw; }
            }

            internal void Bind(Material material)
            {
                material.SetInt("_SourceGridSize", GridSize);
                material.SetBuffer("_SourceTriangles", triangles);
                material.SetBuffer("_SourceCellRanges", cellRanges);
                material.SetBuffer("_SourceCellIndices", cellIndices);
            }

            private static int Cell(float value) => Mathf.Clamp(Mathf.FloorToInt(value * GridSize), 0, GridSize - 1);

            public void Dispose()
            {
                cellIndices?.Release(); cellIndices = null;
                cellRanges?.Release(); cellRanges = null;
                triangles?.Release(); triangles = null;
            }
        }

        private static Mesh CreateTargetMesh(IReadOnlyList<FBXUVTriangle> triangles)
        {
            if (triangles == null || triangles.Count == 0) throw new ArgumentException("転送先の顔三角形がありません。");
            var vertices = new List<Vector3>(triangles.Count * 3);
            var indices = new List<int>(triangles.Count * 3);
            foreach (var triangle in triangles)
            {
                if (triangle == null) throw new ArgumentException("転送先の顔三角形が無効です。");
                for (var i = 0; i < 3; i++)
                {
                    var uv = triangle.GetPoint(i);
                    indices.Add(vertices.Count);
                    vertices.Add(new Vector3(uv.x, uv.y, 0));
                }
            }
            var mesh = new Mesh { name = "FBXUV Makeup Target Mask", hideFlags = HideFlags.HideAndDontSave, indexFormat = IndexFormat.UInt32 };
            try
            {
                mesh.SetVertices(vertices);
                mesh.SetIndices(indices, MeshTopology.Triangles, 0, true);
                return mesh;
            }
            catch { UnityEngine.Object.DestroyImmediate(mesh); throw; }
        }
    }
}
