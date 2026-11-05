using System;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace GokouKotori.FBXUVTextureTransfer
{
    public static class FBXUVBodyOverlayTransferRenderer
    {
        internal const int BleedPixels = 4;
        // Upsampling interpolates only texels whose source UVs are closer than this many overlay pixels (one source island).
        internal const float SameSourceIslandPixels = 4f;
        private const int TransferPass = 0;
        private const int MaskPass = 1;
        private const int CoveragePass = 2;

        internal static void Render(FBXUVBodyOverlayTransferLayer layer, RenderTexture destination, FBXUVBodyCorrespondenceEntry entry)
        {
            if (layer == null) throw new ArgumentNullException(nameof(layer));
            if (destination == null) throw new ArgumentNullException(nameof(destination));
            if (entry == null) throw new ArgumentNullException(nameof(entry));
            var overlay = layer.overlayTexture;
            if (overlay == null) throw new ArgumentException("オーバーレイ画像が未設定です。");
            var shader = layer.ResolveBodyShader();
            if (shader == null || !shader.isSupported) throw new InvalidOperationException("ボディ転送Shaderを利用できません。");
            var computeShader = layer.ResolvePixelProcessShader();
            if (computeShader == null) throw new InvalidOperationException("ボディ転送ComputeShaderを利用できません。");
            if (!destination.IsCreated()) destination.Create();
            // Temporary when the cache has dropped the entry: released below, since the cache will not.
            var gpu = FBXUVBodyOverlayGpuResources.Get(entry, out var temporaryGpu);

            int width = destination.width, height = destination.height;
            var previous = RenderTexture.active;
            var previousSrgb = GL.sRGBWrite;
            Material material = null;
            RenderTexture region = null, work = null, mask = null, seedA = null, seedB = null;
            try
            {
                material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
                gpu.Bind(material);
                material.SetTexture("_MainTex", overlay);
                material.SetVector("_SourceSize", new Vector4(overlay.width, overlay.height, 0, 0));
                material.SetVector("_SourceBounds", new Vector4(0, 0, 1, 1));
                material.SetFloat("_RestoreSRGB", GraphicsFormatUtility.IsSRGBFormat(overlay.graphicsFormat) ? 1f : 0f);
                material.SetVector("_OutputSize", new Vector4(width, height, 0, 0));
                material.SetFloat("_SameSourceIslandPixels", SameSourceIslandPixels);
                region = FBXUVTextureTransferRenderer.GetColorTemporary(destination, width, height, "FBXUV Body Region");
                work = FBXUVTextureTransferRenderer.GetColorTemporary(destination, width, height, "FBXUV Body Work");
                mask = FBXUVTextureTransferRenderer.GetMaskTemporary(width, height, "FBXUV Body Target And Coverage Mask");
                seedA = FBXUVTextureTransferRenderer.GetSeedTemporary(width, height, "FBXUV Body Seed A");
                seedB = FBXUVTextureTransferRenderer.GetSeedTemporary(width, height, "FBXUV Body Seed B");

                // TTT expects raw straight RGBA values, regardless of the project's working color space.
                GL.sRGBWrite = false;
                Draw(region, material, TransferPass, gpu.Quad, true);
                Draw(mask, material, MaskPass, gpu.TargetMesh, true);
                Draw(mask, material, CoveragePass, gpu.Quad, false);
                var filled = new FBXUVPixelProcessor(computeShader).FillAndBleed(region, mask, work, region, seedA, seedB, BleedPixels);
                Copy(filled, destination);
            }
            finally
            {
                GL.sRGBWrite = previousSrgb;
                Graphics.SetRenderTarget(previous);
                FBXUVTextureTransferRenderer.ReleaseTemporary(seedB);
                FBXUVTextureTransferRenderer.ReleaseTemporary(seedA);
                FBXUVTextureTransferRenderer.ReleaseTemporary(mask);
                FBXUVTextureTransferRenderer.ReleaseTemporary(work);
                FBXUVTextureTransferRenderer.ReleaseTemporary(region);
                if (material != null) UnityEngine.Object.DestroyImmediate(material);
                if (temporaryGpu) gpu.Dispose();
            }
        }

        // Re-uploads a texel rectangle of the current SourceUv/Valid. Before the first render there is nothing
        // on the GPU yet, and the lazy creation uploads the current map, so this is a no-op then.
        internal static void UploadRegion(FBXUVBodyCorrespondenceEntry entry, RectInt texels)
        {
            if (entry == null) throw new ArgumentNullException(nameof(entry));
            if (entry.Gpu is FBXUVBodyOverlayGpuResources gpu && gpu.IsAlive) gpu.Upload(entry.Correspondence, texels);
        }

        private static void Draw(RenderTexture target, Material material, int pass, Mesh mesh, bool clear)
        {
            Graphics.SetRenderTarget(target);
            if (clear) GL.Clear(false, true, Color.clear);
            if (!material.SetPass(pass)) throw new InvalidOperationException("ボディ転送Shader passを利用できません。");
            Graphics.DrawMeshNow(mesh, Matrix4x4.identity);
        }

        private static void Copy(RenderTexture source, RenderTexture destination)
        {
            if (SystemInfo.copyTextureSupport != CopyTextureSupport.None && destination.antiAliasing <= 1
                && source.graphicsFormat == destination.graphicsFormat && destination.depth == 0)
                Graphics.CopyTexture(source, 0, 0, destination, 0, 0);
            else
                Graphics.Blit(source, destination);
        }

        // CPU reference for tests (FBXUVBodyOverlayCpuReference): the same rules as the shader and PixelProcess.compute.
        internal static Color32[] RenderReferenceOnCpu(FBXUVBodyCorrespondence correspondence, Texture2D readableOverlay, int width, int height)
            => FBXUVBodyOverlayCpuReference.Render(correspondence, readableOverlay, width, height);

        // The target island mask as the D3D11 rasterizer draws TargetMesh.
        internal static bool[] RasterizeTargetMask(FBXUVBodyCorrespondence correspondence, int width, int height)
            => FBXUVBodyOverlayCpuReference.RasterizeTargetMask(correspondence, width, height);
    }
}
