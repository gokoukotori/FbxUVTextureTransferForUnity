using System;
using UnityEngine;

namespace GokouKotori.FBXUVTextureTransfer
{
    // Restores the active render target and GL.sRGBWrite on Dispose. The renderers switch render targets while they draw
    // and draw with sRGBWrite off (TTT expects raw straight RGBA), and must leave the caller's state as it was, also when
    // drawing throws. A struct, so the per-draw scopes do not allocate.
    internal readonly struct FBXUVRenderStateScope : IDisposable
    {
        private readonly RenderTexture previousTarget;
        private readonly bool previousSrgbWrite;

        private FBXUVRenderStateScope(RenderTexture previousTarget, bool previousSrgbWrite)
        {
            this.previousTarget = previousTarget;
            this.previousSrgbWrite = previousSrgbWrite;
        }

        internal static FBXUVRenderStateScope Capture() => new FBXUVRenderStateScope(RenderTexture.active, GL.sRGBWrite);

        public void Dispose()
        {
            GL.sRGBWrite = previousSrgbWrite;
            Graphics.SetRenderTarget(previousTarget);
        }
    }
}
