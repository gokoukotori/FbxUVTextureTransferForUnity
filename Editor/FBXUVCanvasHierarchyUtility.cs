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
            canvas = null;
            context = layer;
            var current = layer == null ? null : layer.transform.parent;
            if (current == null)
            {
                error = "Layerを MultiLayerImageCanvas または LayerFolder の子に配置してください。";
                return false;
            }

            while (current != null)
            {
                canvas = current.GetComponent<MultiLayerImageCanvas>();
                if (canvas != null)
                {
                    error = null;
                    context = null;
                    return true;
                }

                if (current.GetComponent<LayerFolder>() == null)
                {
                    error = "Layerから MultiLayerImageCanvas までの全ての中間親には LayerFolder が必要です。";
                    context = current.gameObject;
                    return false;
                }

                current = current.parent;
            }

            error = "親階層に MultiLayerImageCanvas がありません。";
            return false;
        }
    }
}
