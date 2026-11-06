using System.Collections.Generic;
using UnityEngine;

namespace GokouKotori.FBXUVTextureTransfer
{
    internal static class FBXUVRendererMeshUtility
    {
        internal static Mesh GetSharedMesh(Renderer renderer)
        {
            if (renderer == null) return null;
            if (renderer is SkinnedMeshRenderer skinned) return skinned.sharedMesh;
            if (!(renderer is MeshRenderer)) return null;

            var filter = renderer.GetComponent<MeshFilter>();
            // Unity's missing-component objects require its overloaded null check.
            return filter == null ? null : filter.sharedMesh;
        }

        internal static IEnumerable<(Renderer Renderer, Mesh Mesh)> Collect(GameObject root)
        {
            if (root == null) yield break;

            // Keep the normal transfer's candidate order and include inactive objects.
            foreach (var renderer in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                var mesh = GetSharedMesh(renderer);
                if (mesh != null) yield return (renderer, mesh);
            }
            foreach (var renderer in root.GetComponentsInChildren<MeshRenderer>(true))
            {
                var mesh = GetSharedMesh(renderer);
                if (mesh != null) yield return (renderer, mesh);
            }
        }
    }
}
