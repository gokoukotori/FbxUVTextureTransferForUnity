using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace GokouKotori.FBXUVTextureTransfer
{
    // Render texture helpers every transfer renderer uses: clearing, the temporary work textures, and the pixel rectangle
    // of a UV region.
    internal static class FBXUVRenderTextureUtility
    {
        internal static void Clear(RenderTexture destination)
        {
            if (destination == null) throw new ArgumentNullException(nameof(destination));
            using (FBXUVRenderStateScope.Capture())
            {
                Graphics.SetRenderTarget(destination);
                GL.Clear(false, true, Color.clear);
            }
        }

        internal static PixelBounds GetExpandedPixelBounds(FBXUVBounds bounds, int width, int height, int padding)
        {
            var left = Mathf.Clamp(Mathf.FloorToInt(bounds.minU * width) - padding, 0, width - 1);
            var top = Mathf.Clamp(Mathf.FloorToInt((1f - bounds.maxV) * height) - padding, 0, height - 1);
            var right = Mathf.Clamp(Mathf.CeilToInt(bounds.maxU * width) + padding, 0, width - 1);
            var bottom = Mathf.Clamp(Mathf.CeilToInt((1f - bounds.minV) * height) + padding, 0, height - 1);
            return new PixelBounds(left, top, right, bottom);
        }

        internal static RenderTexture GetColorTemporary(RenderTexture formatSource, int width, int height, string name)
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

        internal static RenderTexture GetSeedTemporary(int width, int height, string name)
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

        internal static RenderTexture GetMaskTemporary(int width, int height, string name)
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

        internal static void ReleaseTemporary(RenderTexture texture)
        {
            if (texture == null) return;
            RenderTexture.ReleaseTemporary(texture);
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

        internal struct PixelBounds
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
