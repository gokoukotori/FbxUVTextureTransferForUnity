using System.Collections.Generic;
using System.Collections.ObjectModel;
using UnityEngine;

namespace GokouKotori.FBXUVTextureTransfer
{
    internal readonly struct FBXUVTextureSubmeshes
    {
        internal FBXUVTextureSubmeshes(Renderer renderer, Mesh mesh, IReadOnlyList<int> subMeshIndices)
        {
            Renderer = renderer;
            Mesh = mesh;
            SubMeshIndices = subMeshIndices;
        }

        internal Renderer Renderer { get; }
        internal Mesh Mesh { get; }
        internal IReadOnlyList<int> SubMeshIndices { get; }
    }

    internal static class FBXUVTextureUsage
    {
        internal static List<FBXUVTextureSubmeshes> Collect(GameObject root, Texture texture)
        {
            var result = new List<FBXUVTextureSubmeshes>();
            if (root == null || texture == null) return result;

            var materialMatches = new Dictionary<Material, bool>();
            foreach (var item in FBXUVRendererMeshUtility.Collect(root))
            {
                if (item.Mesh.subMeshCount <= 0) continue;
                var subMeshIndices = new HashSet<int>();
                var materials = item.Renderer.sharedMaterials;
                for (var materialIndex = 0; materialIndex < materials.Length; materialIndex++)
                {
                    if (UsesTexture(materials[materialIndex], texture, materialMatches))
                        subMeshIndices.Add(Mathf.Min(materialIndex, item.Mesh.subMeshCount - 1));
                }
                if (subMeshIndices.Count == 0) continue;
                var sorted = new List<int>(subMeshIndices);
                sorted.Sort();
                result.Add(new FBXUVTextureSubmeshes(item.Renderer, item.Mesh, new ReadOnlyCollection<int>(sorted)));
            }
            return result;
        }

        internal static bool UsesTexture(
            Material material,
            Texture texture,
            IDictionary<Material, bool> materialMatches)
        {
            if (material == null) return false;
            if (materialMatches.TryGetValue(material, out var matches)) return matches;

            matches = false;
            foreach (var propertyId in material.GetTexturePropertyNameIDs())
            {
                if (material.GetTexture(propertyId) != texture) continue;
                matches = true;
                break;
            }

            materialMatches.Add(material, matches);
            return matches;
        }
    }
}
