using System.Collections.Generic;
using UnityEngine;

namespace GokouKotori.FBXUVTextureTransfer.Editor
{
    internal readonly struct FBXUVMeshOption
    {
        internal readonly string Label;
        internal readonly Mesh Mesh;
        internal readonly int SubMeshIndex;

        internal FBXUVMeshOption(string label, Mesh mesh, int subMeshIndex)
        {
            Label = label;
            Mesh = mesh;
            SubMeshIndex = subMeshIndex;
        }
    }

    // Owned by one window. Hierarchy/project changes must invalidate the snapshots;
    // changing a root or Canvas texture also changes the cache key.
    internal sealed class FBXUVMeshCandidateCache
    {
        internal sealed class Snapshot
        {
            internal readonly List<FBXUVMeshOption> Options;
            internal readonly string[] Labels;
            internal readonly string[] ReplacementLabels;
            internal readonly List<FBXUVTargetMeshCandidate> TargetCandidates;

            internal Snapshot(List<FBXUVMeshOption> options, List<FBXUVTargetMeshCandidate> targetCandidates = null)
            {
                Options = options;
                TargetCandidates = targetCandidates;
                Labels = new string[options.Count];
                ReplacementLabels = new string[options.Count + 1];
                ReplacementLabels[0] = "<選択してください>";
                for (var index = 0; index < options.Count; index++)
                {
                    Labels[index] = options[index].Label;
                    ReplacementLabels[index + 1] = options[index].Label;
                }
            }
        }

        private GameObject sourceRoot;
        private GameObject targetRoot;
        private Texture targetTexture;
        private Snapshot source;
        private Snapshot target;

        internal Snapshot Source => source;
        internal Snapshot Target => target;

        internal Snapshot GetSource(GameObject reference, out string error)
        {
            FBXUVModelPrefabReferenceUtility.TryResolveRoot(reference, out var root, out error);
            if (source != null && ReferenceEquals(sourceRoot, root)) return source;

            var options = new List<FBXUVMeshOption>();
            if (root != null)
                foreach (var mesh in FBXUVModelPrefabReferenceUtility.CollectMeshes(root))
                    options.Add(new FBXUVMeshOption(mesh.Label, mesh.Mesh, 0));
            sourceRoot = root;
            return source = new Snapshot(options);
        }

        internal Snapshot GetTarget(Component layer, GameObject reference, Texture texture, out string error)
        {
            var validRoot = FBXUVModelPrefabReferenceUtility.TryResolveRoot(reference, out var root, out error);
            // Recheck before consulting the cache, even if the caller holds an old texture.
            var validCanvas = FBXUVCanvasHierarchyUtility.TryFindCanvas(layer, out _, out var canvasError, out _);
            if (!validCanvas)
            {
                root = null;
                if (validRoot) error = canvasError;
            }
            if (target != null && ReferenceEquals(targetRoot, root) && ReferenceEquals(targetTexture, texture)) return target;

            var candidates = root != null
                ? FBXUVTargetMeshCollector.Collect(root, texture)
                : new List<FBXUVTargetMeshCandidate>();
            var options = new List<FBXUVMeshOption>();
            foreach (var candidate in candidates)
                foreach (var subMeshIndex in candidate.SubMeshIndices)
                    options.Add(new FBXUVMeshOption($"{candidate.Label} / サブメッシュ {subMeshIndex}", candidate.Mesh, subMeshIndex));
            targetRoot = root;
            targetTexture = texture;
            return target = new Snapshot(options, candidates);
        }

        internal void Invalidate()
        {
            source = target = null;
            sourceRoot = targetRoot = null;
            targetTexture = null;
        }
    }
}
