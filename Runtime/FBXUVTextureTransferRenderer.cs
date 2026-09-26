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

        public static void Clear(RenderTexture destination)
        {
            if (destination == null) throw new ArgumentNullException(nameof(destination));
            var previous = RenderTexture.active;
            try
            {
                Graphics.SetRenderTarget(destination);
                GL.Clear(false, true, Color.clear);
            }
            finally
            {
                Graphics.SetRenderTarget(previous);
            }
        }

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

        public static void FillAndBleedForTesting(
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
                    seedA = GetSeedTemporary(source.width, source.height, "FBXUV Seed A");
                    seedB = GetSeedTemporary(source.width, source.height, "FBXUV Seed B");
                    ping = GetColorTemporary(destination, source.width, source.height, "FBXUV Fill Ping");
                    var pixels = new FBXUVPixelProcessor(pixelProcessShader);
                    var current = pixels.FillAndBleed(source, targetMask, ping, destination, seedA, seedB, bleedPixels);
                    if (current != destination) Graphics.Blit(current, destination);
                }
                finally
                {
                    ReleaseTemporary(ping);
                    ReleaseTemporary(seedB);
                    ReleaseTemporary(seedA);
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
                var pixelBounds = GetExpandedPixelBounds(targetBounds, destination.width, destination.height, BleedPixels);
                if (pixelBounds.Width <= 0 || pixelBounds.Height <= 0) return;

                RenderTexture region = null;
                RenderTexture mask = null;
                RenderTexture work = null;
                RenderTexture seedA = null;
                RenderTexture seedB = null;
                try
                {
                    region = GetColorTemporary(destination, pixelBounds.Width, pixelBounds.Height, "FBXUV Region");
                    mask = GetMaskTemporary(pixelBounds.Width, pixelBounds.Height, "FBXUV Target And Coverage Mask");
                    work = GetColorTemporary(destination, pixelBounds.Width, pixelBounds.Height, "FBXUV Work");
                    seedA = GetSeedTemporary(pixelBounds.Width, pixelBounds.Height, "FBXUV Seed A");
                    seedB = GetSeedTemporary(pixelBounds.Width, pixelBounds.Height, "FBXUV Seed B");
                    Clear(region);
                    Clear(mask);
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
                    ReleaseTemporary(seedB);
                    ReleaseTemporary(seedA);
                    ReleaseTemporary(work);
                    ReleaseTemporary(mask);
                    ReleaseTemporary(region);
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
            var previous = RenderTexture.active;
            try
            {
                Graphics.SetRenderTarget(target);
                if (material.SetPass(pass)) Graphics.DrawMeshNow(mesh, Matrix4x4.identity);
            }
            finally
            {
                Graphics.SetRenderTarget(previous);
            }
        }

        private static PixelBounds GetExpandedPixelBounds(FBXUVBounds bounds, int width, int height, int padding)
        {
            var left = Mathf.Clamp(Mathf.FloorToInt(bounds.minU * width) - padding, 0, width - 1);
            var top = Mathf.Clamp(Mathf.FloorToInt((1f - bounds.maxV) * height) - padding, 0, height - 1);
            var right = Mathf.Clamp(Mathf.CeilToInt(bounds.maxU * width) + padding, 0, width - 1);
            var bottom = Mathf.Clamp(Mathf.CeilToInt((1f - bounds.minV) * height) + padding, 0, height - 1);
            return new PixelBounds(left, top, right, bottom);
        }

        private static RenderTexture GetColorTemporary(RenderTexture formatSource, int width, int height, string name)
        {
            var descriptor = formatSource.descriptor;
            descriptor.width = width;
            descriptor.height = height;
            descriptor.depthBufferBits = 0;
            descriptor.msaaSamples = 1;
            descriptor.volumeDepth = 1;
            descriptor.dimension = TextureDimension.Tex2D;
            descriptor.enableRandomWrite = true;
            descriptor.useMipMap = false;
            descriptor.autoGenerateMips = false;
            return GetTemporary(descriptor, name, FilterMode.Bilinear);
        }

        private static RenderTexture GetSeedTemporary(int width, int height, string name)
        {
            var descriptor = new RenderTextureDescriptor(width, height, RenderTextureFormat.ARGBFloat, 0)
            {
                enableRandomWrite = true,
                msaaSamples = 1,
                volumeDepth = 1,
                dimension = TextureDimension.Tex2D,
                useMipMap = false,
                autoGenerateMips = false,
                sRGB = false,
            };
            return GetTemporary(descriptor, name, FilterMode.Point);
        }

        private static RenderTexture GetMaskTemporary(int width, int height, string name)
        {
            var descriptor = new RenderTextureDescriptor(width, height, RenderTextureFormat.ARGB32, 0)
            {
                enableRandomWrite = true,
                msaaSamples = 1,
                volumeDepth = 1,
                dimension = TextureDimension.Tex2D,
                useMipMap = false,
                autoGenerateMips = false,
                sRGB = false,
            };
            return GetTemporary(descriptor, name, FilterMode.Point);
        }

        private static RenderTexture GetTemporary(
            RenderTextureDescriptor descriptor,
            string name,
            FilterMode filterMode)
        {
            var texture = RenderTexture.GetTemporary(descriptor);
            if (texture == null)
            {
                throw new InvalidOperationException("一時RenderTextureを取得できません: " + name);
            }
            texture.name = name;
            texture.hideFlags = HideFlags.HideAndDontSave;
            texture.filterMode = filterMode;
            texture.wrapMode = TextureWrapMode.Clamp;
            return texture;
        }

        private static void ReleaseTemporary(RenderTexture texture)
        {
            if (texture == null) return;
            RenderTexture.ReleaseTemporary(texture);
        }

        private struct PixelBounds
        {
            public readonly int Left;
            public readonly int Top;
            public readonly int Right;
            public readonly int Bottom;
            public int Width { get { return Right - Left + 1; } }
            public int Height { get { return Bottom - Top + 1; } }

            public PixelBounds(int left, int top, int right, int bottom)
            {
                Left = left;
                Top = top;
                Right = right;
                Bottom = bottom;
            }
        }
    }
}
