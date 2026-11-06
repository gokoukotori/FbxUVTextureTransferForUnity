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
                        $"{FBXUVModelPrefabReferenceUtility.HierarchyPath(root.transform, item.Renderer.transform)} ({rendererLabel})", item.Mesh);
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
            foreach (var item in FBXUVTextureUsage.Collect(root, targetTexture))
                result.Add(new FBXUVTargetRendererCandidate(item.Renderer, item.Mesh, item.SubMeshIndices));
            return result;
        }

        internal static bool UsesTexture(
            Material material,
            Texture targetTexture,
            IDictionary<Material, bool> materialMatches)
        {
            return FBXUVTextureUsage.UsesTexture(material, targetTexture, materialMatches);
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
