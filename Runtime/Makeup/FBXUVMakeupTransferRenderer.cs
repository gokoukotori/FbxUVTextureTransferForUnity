using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace GokouKotori.FBXUVTextureTransfer
{
    public static class FBXUVMakeupTransferRenderer
    {
        internal static void Render(FBXUVMakeupTransferLayer layer, RenderTexture destination)
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
                throw new ArgumentException("メイクの透過PNGと転送元・転送先の顔のUV島が必要です。");
            var shader = layer.ResolveTransferShader();
            if (shader == null || !shader.isSupported) throw new InvalidOperationException("メイク転送Shaderを利用できません。");
            RenderResources resources = null;
            var retainResources = reuseResources && layer.IsEnabledInHierarchy;
            try
            {
                // The render target is restored before temporary resources are disposed below.
                using (FBXUVRenderStateScope.Capture())
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
            }
            finally
            {
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
            private FBXUVSourceTriangleMask sourceMask;
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
                    sourceMask = FBXUVSourceTriangleMask.Create(layer.sourceRegion.triangles, 32,
                        "転送元の顔三角形がありません。", "転送元の顔三角形には0〜1の有限UVが必要です。",
                        "転送元の顔三角形が全て退化しています。");
                    sourceMask.Bind(Material);
                    Mesh = CreateTargetMesh(layer.targetRegion.triangles);
                    GpuPoints = mapping.Warp.GetGpuPoints();
                    if (retain)
                    {
                        sourceTriangles = FBXUVTriangleSnapshot.Copy(layer.sourceRegion.triangles);
                        targetTriangles = FBXUVTriangleSnapshot.Copy(layer.targetRegion.triangles);
                        liveResources.Add(this);
                    }
                }
                catch { Dispose(); throw; }
            }

            internal bool Matches(FBXUVMakeupTransferLayer layer, FBXUVMakeupMapping current, Shader shader)
            {
                return Material != null && Mesh != null && Material.shader == shader && ReferenceEquals(mapping, current)
                    && FBXUVTriangleSnapshot.Same(sourceTriangles, layer.sourceRegion.triangles)
                    && FBXUVTriangleSnapshot.Same(targetTriangles, layer.targetRegion.triangles);
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
