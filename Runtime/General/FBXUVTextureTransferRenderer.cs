using System;
using System.Collections.Generic;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using MeshBuffers = GokouKotori.FBXUVTextureTransfer.FBXUVRenderResources.MeshBuffers;

namespace GokouKotori.FBXUVTextureTransfer
{
    public static class FBXUVTextureTransferRenderer
    {
        public const int BleedPixels = 4;
        private static readonly ProfilerMarker RenderMarker = new ProfilerMarker("FBXUVTextureTransfer.Render");
        private static readonly ProfilerMarker RenderRegionMarker = new ProfilerMarker("FBXUVTextureTransfer.RenderRegion");
        private static readonly ProfilerMarker FillAndBleedMarker = new ProfilerMarker("FBXUVTextureTransfer.FillAndBleed");

        // Public API kept for compatibility; the transfer renderers call FBXUVRenderTextureUtility.Clear.
        public static void Clear(RenderTexture destination) => FBXUVRenderTextureUtility.Clear(destination);

        public static void Render(FBXUVTextureTransferLayer layer, RenderTexture destination)
        {
            if (layer == null) throw new ArgumentNullException(nameof(layer));
            if (destination == null) throw new ArgumentNullException(nameof(destination));
            if (!SystemInfo.supportsComputeShaders
                || layer.defaultSourceTexture == null
                || layer.regionBindings == null || layer.regionBindings.Count == 0)
            {
                return;
            }

            var shader = layer.ResolveTriangleShader();
            var computeShader = layer.ResolvePixelProcessShader();
            if (shader == null || computeShader == null) return;

            using (RenderMarker.Auto())
            using (var resources = new FBXUVRenderResources(shader, computeShader))
            {
                for (var index = 0; index < layer.regionBindings.Count; index++)
                {
                    var binding = layer.regionBindings[index];
                    if (binding == null || !binding.enabled || string.IsNullOrWhiteSpace(binding.name)) continue;
                    if (binding.sourceRegion == null || binding.targetRegion == null) continue;
                    RenderRegionCore(layer.defaultSourceTexture, binding.sourceRegion, binding.targetRegion,
                        binding.orientation, destination, resources, binding.ComputationCache);
                }
            }
        }

        public static void RenderRegion(
            Texture2D sourceTexture,
            FBXUVTransferRegion sourceRegion,
            FBXUVTransferRegion targetRegion,
            RenderTexture destination,
            Shader triangleShader,
            ComputeShader pixelProcessShader)
        {
            RenderRegion(
                sourceTexture,
                sourceRegion,
                targetRegion,
                destination,
                triangleShader,
                pixelProcessShader,
                FBXUVTransferOrientation.Preserve);
        }

        public static void RenderRegion(
            Texture2D sourceTexture,
            FBXUVTransferRegion sourceRegion,
            FBXUVTransferRegion targetRegion,
            RenderTexture destination,
            Shader triangleShader,
            ComputeShader pixelProcessShader,
            FBXUVTransferOrientation orientation)
        {
            if (sourceTexture == null) throw new ArgumentNullException(nameof(sourceTexture));
            if (sourceRegion == null) throw new ArgumentNullException(nameof(sourceRegion));
            if (targetRegion == null) throw new ArgumentNullException(nameof(targetRegion));
            if (destination == null) throw new ArgumentNullException(nameof(destination));
            if (triangleShader == null) throw new ArgumentNullException(nameof(triangleShader));
            if (pixelProcessShader == null) throw new ArgumentNullException(nameof(pixelProcessShader));

            using (RenderMarker.Auto())
            using (var resources = new FBXUVRenderResources(triangleShader, pixelProcessShader))
            {
                RenderRegionCore(sourceTexture, sourceRegion, targetRegion, orientation, destination, resources);
            }
        }

        internal static void FillAndBleedForTesting(
            RenderTexture source,
            RenderTexture targetMask,
            RenderTexture destination,
            ComputeShader pixelProcessShader,
            int bleedPixels = BleedPixels)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (targetMask == null) throw new ArgumentNullException(nameof(targetMask));
            if (destination == null) throw new ArgumentNullException(nameof(destination));
            if (pixelProcessShader == null) throw new ArgumentNullException(nameof(pixelProcessShader));
            if (source.width != targetMask.width || source.height != targetMask.height
                || source.width != destination.width || source.height != destination.height)
            {
                throw new ArgumentException("source、targetMask、destinationの寸法は同一である必要があります。");
            }

            using (FillAndBleedMarker.Auto())
            {
                RenderTexture seedA = null;
                RenderTexture seedB = null;
                RenderTexture ping = null;
                try
                {
                    seedA = FBXUVRenderTextureUtility.GetSeedTemporary(source.width, source.height, "FBXUV Seed A");
                    seedB = FBXUVRenderTextureUtility.GetSeedTemporary(source.width, source.height, "FBXUV Seed B");
                    ping = FBXUVRenderTextureUtility.GetColorTemporary(destination, source.width, source.height, "FBXUV Fill Ping");
                    var pixels = new FBXUVPixelProcessor(pixelProcessShader);
                    var current = pixels.FillAndBleed(source, targetMask, ping, destination, seedA, seedB, bleedPixels);
                    if (current != destination) Graphics.Blit(current, destination);
                }
                finally
                {
                    FBXUVRenderTextureUtility.ReleaseTemporary(ping);
                    FBXUVRenderTextureUtility.ReleaseTemporary(seedB);
                    FBXUVRenderTextureUtility.ReleaseTemporary(seedA);
                }
            }
        }

        private static void RenderRegionCore(
            Texture2D sourceTexture,
            FBXUVTransferRegion sourceRegion,
            FBXUVTransferRegion targetRegion,
            FBXUVTransferOrientation orientation,
            RenderTexture destination,
            FBXUVRenderResources resources,
            FBXUVRegionComputationCache cache = null)
        {
            using (RenderRegionMarker.Auto())
            {
                cache?.Prepare(sourceRegion, targetRegion);
                var targetTriangles = cache?.TargetTriangles ?? FBXUVDeformationUtility.GetEffectiveTriangles(targetRegion);
                var sourceTriangles = cache?.SourceTriangles ?? FBXUVDeformationUtility.GetEffectiveTriangles(sourceRegion);
                if (targetTriangles.Count == 0 || sourceTriangles.Count == 0) return;
                var targetBounds = targetRegion.bounds.IsValid ? targetRegion.bounds : FBXUVBounds.FromTriangles(targetTriangles);
                if (!targetBounds.IsValid) return;
                var pixelBounds = FBXUVRenderTextureUtility.GetExpandedPixelBounds(targetBounds, destination.width, destination.height, BleedPixels);
                if (pixelBounds.Width <= 0 || pixelBounds.Height <= 0) return;

                RenderTexture region = null;
                RenderTexture mask = null;
                RenderTexture work = null;
                RenderTexture seedA = null;
                RenderTexture seedB = null;
                try
                {
                    region = FBXUVRenderTextureUtility.GetColorTemporary(destination, pixelBounds.Width, pixelBounds.Height, "FBXUV Region");
                    mask = FBXUVRenderTextureUtility.GetMaskTemporary(pixelBounds.Width, pixelBounds.Height, "FBXUV Target And Coverage Mask");
                    work = FBXUVRenderTextureUtility.GetColorTemporary(destination, pixelBounds.Width, pixelBounds.Height, "FBXUV Work");
                    seedA = FBXUVRenderTextureUtility.GetSeedTemporary(pixelBounds.Width, pixelBounds.Height, "FBXUV Seed A");
                    seedB = FBXUVRenderTextureUtility.GetSeedTemporary(pixelBounds.Width, pixelBounds.Height, "FBXUV Seed B");
                    FBXUVRenderTextureUtility.Clear(region);
                    FBXUVRenderTextureUtility.Clear(mask);
                    var vertexMap = cache != null
                        ? cache.GetVertexMap(destination.width, destination.height, orientation)
                        : FBXUVDeformationUtility.CreateKeyedVertexMap(
                        sourceRegion,
                        targetRegion,
                        destination.width,
                        destination.height,
                        orientation);
                    if (vertexMap.Count == 0) return;
                    UpdateTransferMesh(resources.TransferMesh, sourceTriangles, vertexMap, pixelBounds.Left, pixelBounds.Top, resources.Buffers);
                    UpdateMaskMesh(resources.MaskMesh, targetTriangles, destination.width, destination.height, pixelBounds.Left, pixelBounds.Top, resources.Buffers);
                    DrawMaskMesh(resources.MaskMesh, mask, resources.Material);
                    DrawCoverageMesh(resources.TransferMesh, mask, resources.Material);
                    DrawTransferMesh(resources.TransferMesh, sourceTexture, region, resources.Material);

                    var filled = resources.Pixels.FillAndBleed(region, mask, work, region, seedA, seedB, BleedPixels);
                    resources.Pixels.CompositeStraightAlpha(filled, destination, pixelBounds.Left, pixelBounds.Top);
                }
                finally
                {
                    FBXUVRenderTextureUtility.ReleaseTemporary(seedB);
                    FBXUVRenderTextureUtility.ReleaseTemporary(seedA);
                    FBXUVRenderTextureUtility.ReleaseTemporary(work);
                    FBXUVRenderTextureUtility.ReleaseTemporary(mask);
                    FBXUVRenderTextureUtility.ReleaseTemporary(region);
                }
            }
        }

        private static void UpdateTransferMesh(
            Mesh mesh,
            IReadOnlyList<FBXUVTriangle> triangles,
            IReadOnlyDictionary<FBXUVDeformationUtility.VertexKey, FBXUVDeformedVertex> vertexMap,
            int offsetX,
            int offsetY,
            MeshBuffers buffers)
        {
            var count = triangles.Count * 3;
            buffers.Prepare(count);
            for (var triangleIndex = 0; triangleIndex < triangles.Count; triangleIndex++)
            {
                var triangle = triangles[triangleIndex];
                AddTransferVertex(triangle.a, vertexMap, buffers, offsetX, offsetY);
                AddTransferVertex(triangle.b, vertexMap, buffers, offsetX, offsetY);
                AddTransferVertex(triangle.c, vertexMap, buffers, offsetX, offsetY);
            }
            UpdateMesh(mesh, buffers, count);
        }

        private static void AddTransferVertex(
            Vector2 point,
            IReadOnlyDictionary<FBXUVDeformationUtility.VertexKey, FBXUVDeformedVertex> vertexMap,
            MeshBuffers buffers,
            int offsetX,
            int offsetY)
        {
            var vertex = vertexMap[new FBXUVDeformationUtility.VertexKey(point)];
            var vertexIndex = buffers.Positions.Count;
            buffers.Positions.Add(new Vector3(vertex.TargetPixel.x - offsetX, vertex.TargetPixel.y - offsetY, 0f));
            buffers.Uvs.Add(vertex.SourceUv);
            buffers.Indices.Add(vertexIndex);
        }

        private static void UpdateMaskMesh(
            Mesh mesh,
            IReadOnlyList<FBXUVTriangle> triangles,
            int width,
            int height,
            int offsetX,
            int offsetY,
            MeshBuffers buffers)
        {
            var count = triangles.Count * 3;
            buffers.Prepare(count);
            for (var triangleIndex = 0; triangleIndex < triangles.Count; triangleIndex++)
            {
                var triangle = triangles[triangleIndex];
                for (var pointIndex = 0; pointIndex < 3; pointIndex++)
                {
                    var pixel = FBXUVDeformationUtility.UvToTopLeftPixel(triangle.GetPoint(pointIndex), width, height);
                    var vertexIndex = buffers.Positions.Count;
                    buffers.Positions.Add(new Vector3(pixel.x - offsetX, pixel.y - offsetY, 0f));
                    buffers.Uvs.Add(Vector2.zero);
                    buffers.Indices.Add(vertexIndex);
                }
            }
            UpdateMesh(mesh, buffers, count);
        }

        private static void UpdateMesh(Mesh mesh, MeshBuffers buffers, int vertexCount)
        {
            mesh.Clear();
            mesh.indexFormat = vertexCount > ushort.MaxValue ? IndexFormat.UInt32 : IndexFormat.UInt16;
            mesh.SetVertices(buffers.Positions);
            mesh.SetUVs(0, buffers.Uvs);
            mesh.SetIndices(buffers.Indices, MeshTopology.Triangles, 0, true);
            mesh.UploadMeshData(false);
        }

        private static void DrawTransferMesh(Mesh mesh, Texture2D sourceTexture, RenderTexture target, Material material)
        {
            material.SetTexture("_MainTex", sourceTexture);
            material.SetVector("_OutputSize", new Vector4(target.width, target.height, 1f / target.width, 1f / target.height));
            material.SetFloat("_RestoreSRGB", GraphicsFormatUtility.IsSRGBFormat(sourceTexture.graphicsFormat) ? 1f : 0f);
            DrawMesh(mesh, target, material, 0);
        }

        private static void DrawMaskMesh(Mesh mesh, RenderTexture target, Material material)
        {
            material.SetVector("_OutputSize", new Vector4(target.width, target.height, 1f / target.width, 1f / target.height));
            DrawMesh(mesh, target, material, 1);
        }

        private static void DrawCoverageMesh(Mesh mesh, RenderTexture target, Material material)
        {
            material.SetVector("_OutputSize", new Vector4(target.width, target.height, 1f / target.width, 1f / target.height));
            DrawMesh(mesh, target, material, 2);
        }

        private static void DrawMesh(Mesh mesh, RenderTexture target, Material material, int pass)
        {
            using (FBXUVRenderStateScope.Capture())
            {
                Graphics.SetRenderTarget(target);
                if (material.SetPass(pass)) Graphics.DrawMeshNow(mesh, Matrix4x4.identity);
            }
        }

    }
}
