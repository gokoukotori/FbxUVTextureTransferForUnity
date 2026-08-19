using System;
using System.Collections.Generic;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace GokouKotori.FBXUVTextureTransfer
{
    public static class FBXUVTextureTransferRenderer
    {
        public const int BleedPixels = 4;
        private const int ThreadGroupSize = 8;
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
            {
                Material material = null;
                Mesh transferMesh = null;
                Mesh maskMesh = null;
                try
                {
                    material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
                    transferMesh = CreateReusableMesh("FBXUV Transfer Mesh");
                    maskMesh = CreateReusableMesh("FBXUV Mask Mesh");
                    var buffers = new MeshBuffers();
                    var kernels = new ComputeKernels(computeShader);
                    for (var index = 0; index < layer.regionBindings.Count; index++)
                    {
                        var binding = layer.regionBindings[index];
                        if (binding == null || !binding.enabled || string.IsNullOrWhiteSpace(binding.name)) continue;
                        if (binding.sourceRegion == null || binding.targetRegion == null) continue;
                        RenderRegionCore(
                            layer.defaultSourceTexture,
                            binding.sourceRegion,
                            binding.targetRegion,
                            binding.orientation,
                            destination,
                            material,
                            computeShader,
                            kernels,
                            transferMesh,
                            maskMesh,
                            buffers);
                    }
                }
                finally
                {
                    if (maskMesh != null) UnityEngine.Object.DestroyImmediate(maskMesh);
                    if (transferMesh != null) UnityEngine.Object.DestroyImmediate(transferMesh);
                    if (material != null) UnityEngine.Object.DestroyImmediate(material);
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
            {
                Material material = null;
                Mesh transferMesh = null;
                Mesh maskMesh = null;
                try
                {
                    material = new Material(triangleShader) { hideFlags = HideFlags.HideAndDontSave };
                    transferMesh = CreateReusableMesh("FBXUV Transfer Mesh");
                    maskMesh = CreateReusableMesh("FBXUV Mask Mesh");
                    RenderRegionCore(
                        sourceTexture,
                        sourceRegion,
                        targetRegion,
                        orientation,
                        destination,
                        material,
                        pixelProcessShader,
                        new ComputeKernels(pixelProcessShader),
                        transferMesh,
                        maskMesh,
                        new MeshBuffers());
                }
                finally
                {
                    if (maskMesh != null) UnityEngine.Object.DestroyImmediate(maskMesh);
                    if (transferMesh != null) UnityEngine.Object.DestroyImmediate(transferMesh);
                    if (material != null) UnityEngine.Object.DestroyImmediate(material);
                }
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
                    var kernels = new ComputeKernels(pixelProcessShader);
                    var resolved = FillTransparentTargetPixels(source, targetMask, ping, seedA, seedB, pixelProcessShader, kernels);
                    var current = resolved;
                    var other = destination;
                    for (var pass = 0; pass < bleedPixels; pass++)
                    {
                        Dilate(current, other, pixelProcessShader, kernels);
                        var swap = current;
                        current = other;
                        other = swap;
                    }
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
            Material material,
            ComputeShader pixelProcessShader,
            ComputeKernels kernels,
            Mesh transferMesh,
            Mesh maskMesh,
            MeshBuffers buffers)
        {
            using (RenderRegionMarker.Auto())
            {
                var targetTriangles = FBXUVDeformationUtility.GetEffectiveTriangles(targetRegion);
                var sourceTriangles = FBXUVDeformationUtility.GetEffectiveTriangles(sourceRegion);
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
                    mask = GetColorTemporary(destination, pixelBounds.Width, pixelBounds.Height, "FBXUV Target Mask");
                    work = GetColorTemporary(destination, pixelBounds.Width, pixelBounds.Height, "FBXUV Work");
                    seedA = GetSeedTemporary(pixelBounds.Width, pixelBounds.Height, "FBXUV Seed A");
                    seedB = GetSeedTemporary(pixelBounds.Width, pixelBounds.Height, "FBXUV Seed B");
                    Clear(region);
                    Clear(mask);
                    var vertexMap = FBXUVDeformationUtility.CreateKeyedVertexMap(
                        sourceRegion,
                        targetRegion,
                        destination.width,
                        destination.height,
                        orientation);
                    if (vertexMap.Count == 0) return;
                    UpdateTransferMesh(transferMesh, sourceTriangles, vertexMap, pixelBounds.Left, pixelBounds.Top, buffers);
                    UpdateMaskMesh(maskMesh, targetTriangles, destination.width, destination.height, pixelBounds.Left, pixelBounds.Top, buffers);
                    DrawTransferMesh(transferMesh, sourceTexture, region, material);
                    DrawMaskMesh(maskMesh, mask, material);

                    FillTransparentTargetPixels(region, mask, work, seedA, seedB, pixelProcessShader, kernels);
                    var input = work;
                    var output = region;
                    for (var pass = 0; pass < BleedPixels; pass++)
                    {
                        Dilate(input, output, pixelProcessShader, kernels);
                        var swap = input;
                        input = output;
                        output = swap;
                    }
                    CompositeStraightAlpha(input, destination, pixelBounds.Left, pixelBounds.Top, pixelProcessShader, kernels);
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

        private static Mesh CreateReusableMesh(string name)
        {
            return new Mesh { name = name, hideFlags = HideFlags.HideAndDontSave };
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

        private static RenderTexture FillTransparentTargetPixels(
            RenderTexture source,
            RenderTexture mask,
            RenderTexture output,
            RenderTexture seedA,
            RenderTexture seedB,
            ComputeShader computeShader,
            ComputeKernels kernels)
        {
            var initialize = kernels.InitializeSeeds;
            SetTextureSize(computeShader, source.width, source.height);
            computeShader.SetTexture(initialize, "_Source", source);
            computeShader.SetTexture(initialize, "_Mask", mask);
            computeShader.SetTexture(initialize, "_SeedWrite", seedA);
            Dispatch(computeShader, initialize, source.width, source.height);

            var propagate = kernels.PropagateSeeds;
            var read = seedA;
            var write = seedB;
            for (var jump = HighestJump(source.width, source.height); jump >= 1; jump >>= 1)
            {
                computeShader.SetInt("_Jump", jump);
                computeShader.SetTexture(propagate, "_Mask", mask);
                computeShader.SetTexture(propagate, "_SeedRead", read);
                computeShader.SetTexture(propagate, "_SeedWrite", write);
                Dispatch(computeShader, propagate, source.width, source.height);
                var swap = read;
                read = write;
                write = swap;
            }

            var resolve = kernels.ResolveFill;
            computeShader.SetTexture(resolve, "_Source", source);
            computeShader.SetTexture(resolve, "_Mask", mask);
            computeShader.SetTexture(resolve, "_SeedRead", read);
            computeShader.SetTexture(resolve, "_Output", output);
            Dispatch(computeShader, resolve, source.width, source.height);
            return output;
        }

        private static void Dilate(
            RenderTexture source,
            RenderTexture output,
            ComputeShader computeShader,
            ComputeKernels kernels)
        {
            var kernel = kernels.Dilate8Connected;
            SetTextureSize(computeShader, source.width, source.height);
            computeShader.SetTexture(kernel, "_Source", source);
            computeShader.SetTexture(kernel, "_Output", output);
            Dispatch(computeShader, kernel, source.width, source.height);
        }

        private static void CompositeStraightAlpha(
            RenderTexture source,
            RenderTexture destination,
            int offsetX,
            int offsetY,
            ComputeShader computeShader,
            ComputeKernels kernels)
        {
            var kernel = kernels.CompositeStraightAlpha;
            SetTextureSize(computeShader, source.width, source.height);
            computeShader.SetInts("_DestinationOffset", offsetX, offsetY);
            computeShader.SetInts("_DestinationSize", destination.width, destination.height);
            computeShader.SetTexture(kernel, "_Source", source);
            computeShader.SetTexture(kernel, "_Destination", destination);
            Dispatch(computeShader, kernel, source.width, source.height);
        }

        private static void SetTextureSize(ComputeShader shader, int width, int height)
        {
            shader.SetInts("_TextureSize", width, height);
        }

        private static void Dispatch(ComputeShader shader, int kernel, int width, int height)
        {
            shader.Dispatch(kernel, (width + ThreadGroupSize - 1) / ThreadGroupSize, (height + ThreadGroupSize - 1) / ThreadGroupSize, 1);
        }

        private static int HighestJump(int width, int height)
        {
            var maximum = Mathf.Max(width, height);
            var jump = 1;
            while (jump < maximum && jump <= (int.MaxValue >> 1)) jump <<= 1;
            return Mathf.Max(1, jump >> 1);
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

        private sealed class MeshBuffers
        {
            public readonly List<Vector3> Positions = new List<Vector3>();
            public readonly List<Vector2> Uvs = new List<Vector2>();
            public readonly List<int> Indices = new List<int>();

            public void Prepare(int capacity)
            {
                Positions.Clear();
                Uvs.Clear();
                Indices.Clear();
                EnsureCapacity(Positions, capacity);
                EnsureCapacity(Uvs, capacity);
                EnsureCapacity(Indices, capacity);
            }

            private static void EnsureCapacity<T>(List<T> list, int capacity)
            {
                if (list.Capacity < capacity) list.Capacity = capacity;
            }
        }

        private sealed class ComputeKernels
        {
            private readonly ComputeShader shader;
            private int initializeSeeds;
            private int propagateSeeds;
            private int resolveFill;
            private int dilate8Connected;
            private int compositeStraightAlpha;
            private bool hasInitializeSeeds;
            private bool hasPropagateSeeds;
            private bool hasResolveFill;
            private bool hasDilate8Connected;
            private bool hasCompositeStraightAlpha;

            public ComputeKernels(ComputeShader shader)
            {
                this.shader = shader;
            }

            public int InitializeSeeds
            {
                get { return Resolve("InitializeSeeds", ref initializeSeeds, ref hasInitializeSeeds); }
            }

            public int PropagateSeeds
            {
                get { return Resolve("PropagateSeeds", ref propagateSeeds, ref hasPropagateSeeds); }
            }

            public int ResolveFill
            {
                get { return Resolve("ResolveFill", ref resolveFill, ref hasResolveFill); }
            }

            public int Dilate8Connected
            {
                get { return Resolve("Dilate8Connected", ref dilate8Connected, ref hasDilate8Connected); }
            }

            public int CompositeStraightAlpha
            {
                get { return Resolve("CompositeStraightAlpha", ref compositeStraightAlpha, ref hasCompositeStraightAlpha); }
            }

            private int Resolve(string name, ref int kernel, ref bool resolved)
            {
                if (!resolved)
                {
                    kernel = shader.FindKernel(name);
                    resolved = true;
                }
                return kernel;
            }
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
