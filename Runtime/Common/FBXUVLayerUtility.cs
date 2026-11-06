using System;
using net.rs64.TexTransTool.MultiLayerImage;
using UnityEngine;

namespace GokouKotori.FBXUVTextureTransfer
{
    internal static class FBXUVLayerUtility
    {
        internal static bool HasExclusiveImplementation(MonoBehaviour layer)
        {
            var count = 0;
            foreach (var component in layer.GetComponents<MonoBehaviour>())
                if (component is IExternalToolCanBehaveAsLayer) count++;
            return count == 1;
        }

        internal static void EnsureExternalToolAsLayer(GameObject owner)
        {
            if (owner.GetComponent<ExternalToolAsLayer>() == null) owner.AddComponent<ExternalToolAsLayer>();
        }

        internal static void LoadImage(RenderTexture destination, MonoBehaviour layer, Action<RenderTexture> render,
            string failureMessage, string fallbackMessage)
        {
            if (destination == null) return;
            try
            {
                FBXUVRenderTextureUtility.Clear(destination);
                render(destination);
            }
            catch (Exception exception)
            {
                try { FBXUVRenderTextureUtility.Clear(destination); }
                catch (Exception clearException) { Debug.LogError(fallbackMessage + "\n" + clearException, layer); }
                Debug.LogError(failureMessage + "\n" + exception, layer);
            }
        }
    }
}
