using System.Collections.Generic;
using System.Collections.ObjectModel;
using UnityEngine;

namespace GokouKotori.FBXUVTextureTransfer.Editor
{
    internal sealed class FBXUVTargetMeshCandidate
    {
        internal FBXUVTargetMeshCandidate(
            string label,
            Mesh mesh,
            IReadOnlyList<int> subMeshIndices)
        {
            Label = label;
            Mesh = mesh;
            SubMeshIndices = subMeshIndices;
        }

        internal string Label { get; }
        internal Mesh Mesh { get; }
        internal IReadOnlyList<int> SubMeshIndices { get; }
    }

    internal sealed class FBXUVTargetRendererCandidate
    {
        internal FBXUVTargetRendererCandidate(Renderer renderer, Mesh mesh, IReadOnlyList<int> subMeshIndices)
        {
            Renderer = renderer;
            Mesh = mesh;
            SubMeshIndices = subMeshIndices;
        }

        internal Renderer Renderer { get; }
        internal Mesh Mesh { get; }
        internal IReadOnlyList<int> SubMeshIndices { get; }
    }

    internal static class FBXUVTargetMeshCollector
    {
        internal static List<FBXUVTargetMeshCandidate> Collect(
            GameObject root,
            Texture targetTexture)
        {
            var result = new List<FBXUVTargetMeshCandidate>();
            if (root == null || targetTexture == null) return result;

            var candidateByMesh = new Dictionary<Mesh, MutableCandidate>();
            var candidates = new List<MutableCandidate>();

            foreach (var item in CollectRenderers(root, targetTexture))
            {
                if (!candidateByMesh.TryGetValue(item.Mesh, out var candidate))
                {
                    var rendererLabel = item.Renderer is SkinnedMeshRenderer ? "Skinned" : "MeshFilter";
                    candidate = new MutableCandidate(
                        $"{HierarchyPath(root.transform, item.Renderer.transform)} ({rendererLabel})", item.Mesh);
                    candidateByMesh.Add(item.Mesh, candidate);
                    candidates.Add(candidate);
                }
                candidate.SubMeshIndices.UnionWith(item.SubMeshIndices);
            }

            foreach (var candidate in candidates)
            {
                var subMeshIndices = new List<int>(candidate.SubMeshIndices);
                subMeshIndices.Sort();
                result.Add(new FBXUVTargetMeshCandidate(
                    candidate.Label,
                    candidate.Mesh,
                    new ReadOnlyCollection<int>(subMeshIndices)));
            }

            return result;
        }

        internal static List<FBXUVTargetRendererCandidate> CollectRenderers(GameObject root, Texture targetTexture)
        {
            var result = new List<FBXUVTargetRendererCandidate>();
            if (root == null || targetTexture == null) return result;

            var materialMatches = new Dictionary<Material, bool>();
            foreach (var item in FBXUVRendererMeshUtility.Collect(root))
            {
                if (item.Mesh.subMeshCount <= 0) continue;
                var subMeshIndices = new HashSet<int>();
                var materials = item.Renderer.sharedMaterials;
                for (var materialIndex = 0; materialIndex < materials.Length; materialIndex++)
                {
                    if (UsesTexture(materials[materialIndex], targetTexture, materialMatches))
                        subMeshIndices.Add(Mathf.Min(materialIndex, item.Mesh.subMeshCount - 1));
                }
                if (subMeshIndices.Count == 0) continue;
                var sorted = new List<int>(subMeshIndices);
                sorted.Sort();
                result.Add(new FBXUVTargetRendererCandidate(item.Renderer, item.Mesh, new ReadOnlyCollection<int>(sorted)));
            }
            return result;
        }

        internal static bool UsesTexture(
            Material material,
            Texture targetTexture,
            IDictionary<Material, bool> materialMatches)
        {
            if (material == null) return false;
            if (materialMatches.TryGetValue(material, out var matches)) return matches;

            matches = false;
            foreach (var propertyId in material.GetTexturePropertyNameIDs())
            {
                if (material.GetTexture(propertyId) != targetTexture) continue;
                matches = true;
                break;
            }

            materialMatches.Add(material, matches);
            return matches;
        }

        private static string HierarchyPath(Transform root, Transform target)
        {
            if (target == root) return target.name;

            var names = new Stack<string>();
            var current = target;
            while (current != null)
            {
                names.Push(current.name);
                if (current == root) break;
                current = current.parent;
            }

            return string.Join("/", names);
        }

        private sealed class MutableCandidate
        {
            internal MutableCandidate(string label, Mesh mesh)
            {
                Label = label;
                Mesh = mesh;
                SubMeshIndices = new HashSet<int>();
            }

            internal string Label { get; }
            internal Mesh Mesh { get; }
            internal HashSet<int> SubMeshIndices { get; }
        }
    }
}
