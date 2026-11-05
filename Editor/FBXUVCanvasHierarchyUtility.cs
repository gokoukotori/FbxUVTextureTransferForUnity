using net.rs64.TexTransTool.MultiLayerImage;
using UnityEngine;

namespace GokouKotori.FBXUVTextureTransfer.Editor
{
    internal static class FBXUVCanvasHierarchyUtility
    {
        internal static bool TryFindCanvas(
            FBXUVTextureTransferLayer layer,
            out MultiLayerImageCanvas canvas,
            out string error,
            out Object context)
        {
            return TryFindCanvas((Component)layer, out canvas, out error, out context);
        }

        internal static bool TryFindCanvas(
            Component layer,
            out MultiLayerImageCanvas canvas,
            out string error,
            out Object context)
        {
            return FBXUVCanvasTargetUtility.TryFindCanvas(layer, out canvas, out error, out context);
        }

        internal static Texture2D ResolveTargetTexture(Component layer)
        {
            return FBXUVCanvasTargetUtility.ResolveTargetTexture(layer);
        }
    }
}
