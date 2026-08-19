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

    internal static class FBXUVTargetMeshCollector
    {
        internal static List<FBXUVTargetMeshCandidate> Collect(
            GameObject root,
            Texture targetTexture)
        {
            var result = new List<FBXUVTargetMeshCandidate>();
            if (root == null || targetTexture == null) return result;

            var materialMatches = new Dictionary<Material, bool>();
            var candidateByMesh = new Dictionary<Mesh, MutableCandidate>();
            var candidates = new List<MutableCandidate>();

            foreach (var renderer in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                AddRenderer(
                    root.transform,
                    renderer,
                    renderer.sharedMesh,
                    "Skinned",
                    targetTexture,
                    materialMatches,
                    candidateByMesh,
                    candidates);
            }

            foreach (var renderer in root.GetComponentsInChildren<MeshRenderer>(true))
            {
                var filter = renderer.GetComponent<MeshFilter>();
                if (filter == null) continue;

                AddRenderer(
                    root.transform,
                    renderer,
                    filter.sharedMesh,
                    "MeshFilter",
                    targetTexture,
                    materialMatches,
                    candidateByMesh,
                    candidates);
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

        private static void AddRenderer(
            Transform root,
            Renderer renderer,
            Mesh mesh,
            string rendererLabel,
            Texture targetTexture,
            IDictionary<Material, bool> materialMatches,
            IDictionary<Mesh, MutableCandidate> candidateByMesh,
            ICollection<MutableCandidate> candidates)
        {
            if (renderer == null || mesh == null || mesh.subMeshCount <= 0) return;

            MutableCandidate candidate = null;
            var materials = renderer.sharedMaterials;
            for (var materialIndex = 0; materialIndex < materials.Length; materialIndex++)
            {
                if (!UsesTexture(materials[materialIndex], targetTexture, materialMatches)) continue;

                if (candidate == null && !candidateByMesh.TryGetValue(mesh, out candidate))
                {
                    candidate = new MutableCandidate(
                        $"{HierarchyPath(root, renderer.transform)} ({rendererLabel})",
                        mesh);
                    candidateByMesh.Add(mesh, candidate);
                    candidates.Add(candidate);
                }

                candidate.SubMeshIndices.Add(Mathf.Min(materialIndex, mesh.subMeshCount - 1));
            }
        }

        private static bool UsesTexture(
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
