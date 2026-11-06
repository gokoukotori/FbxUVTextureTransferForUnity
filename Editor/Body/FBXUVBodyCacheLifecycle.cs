using UnityEditor;

namespace GokouKotori.FBXUVTextureTransfer
{
    [InitializeOnLoad]
    internal static class FBXUVBodyCacheLifecycle
    {
        static FBXUVBodyCacheLifecycle()
        {
            AssemblyReloadEvents.beforeAssemblyReload -= FBXUVBodyCorrespondenceCache.Clear;
            AssemblyReloadEvents.beforeAssemblyReload += FBXUVBodyCorrespondenceCache.Clear;
        }
    }
}
