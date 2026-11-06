using UnityEditor;

namespace GokouKotori.FBXUVTextureTransfer.Editor
{
    // Components need not execute in Edit Mode for their preview resources to be
    // released after deletion, disabling, play transitions or assembly reload.
    [InitializeOnLoad]
    internal static class FBXUVMakeupRenderResourceLifecycle
    {
        static FBXUVMakeupRenderResourceLifecycle()
        {
            EditorApplication.update += FBXUVMakeupTransferRenderer.ReleaseUnusedResources;
            AssemblyReloadEvents.beforeAssemblyReload += FBXUVMakeupTransferRenderer.ReleaseAllResources;
            EditorApplication.quitting += FBXUVMakeupTransferRenderer.ReleaseAllResources;
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
        }

        private static void OnPlayModeChanged(PlayModeStateChange state)
        {
            FBXUVMakeupTransferRenderer.ReleaseAllResources();
        }
    }
}
