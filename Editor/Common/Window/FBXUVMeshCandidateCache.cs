using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace GokouKotori.FBXUVTextureTransfer.Editor
{
    internal enum FBXUVCandidateGrouping
    {
        ByMesh,
        ByRenderer
    }

    internal readonly struct FBXUVMeshOption
    {
        internal readonly string Label;
        internal readonly Mesh Mesh;
        internal readonly int SubMeshIndex;
        internal readonly string RendererPath;

        internal FBXUVMeshOption(string label, Mesh mesh, int subMeshIndex)
            : this(label, mesh, subMeshIndex, null)
        {
        }

        internal FBXUVMeshOption(string label, Mesh mesh, int subMeshIndex, string rendererPath)
        {
            Label = label;
            Mesh = mesh;
            SubMeshIndex = subMeshIndex;
            RendererPath = rendererPath;
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

            internal Snapshot(List<FBXUVMeshOption> options)
            {
                Options = options;
                Labels = new string[options.Count];
                ReplacementLabels = new string[options.Count + 1];
                ReplacementLabels[0] = "<選択してください>";
                for (var index = 0; index < options.Count; index++)
                {
                    Labels[index] = options[index].Label;
                    ReplacementLabels[index + 1] = options[index].Label;
                }
            }

            // A null rendererPath matches any renderer.
            internal int IndexOf(Mesh mesh, int subMeshIndex, string rendererPath)
            {
                for (var index = 0; index < Options.Count; index++)
                {
                    var option = Options[index];
                    if (option.Mesh != mesh || option.SubMeshIndex != subMeshIndex) continue;
                    if (rendererPath != null && option.RendererPath != rendererPath) continue;
                    return index;
                }

                return -1;
            }
        }

        private sealed class GroupedEntry
        {
            internal GameObject Root;
            internal Texture Texture;
            internal Snapshot Snapshot;
        }

        private readonly Dictionary<FBXUVCandidateGrouping, GroupedEntry> groupedSources =
            new Dictionary<FBXUVCandidateGrouping, GroupedEntry>();
        private readonly Dictionary<FBXUVCandidateGrouping, GroupedEntry> groupedTargets =
            new Dictionary<FBXUVCandidateGrouping, GroupedEntry>();

        internal Snapshot GetSource(GameObject reference, FBXUVCandidateGrouping grouping, out string error)
        {
            FBXUVModelPrefabReferenceUtility.TryResolveRoot(reference, out var root, out error);
            if (groupedSources.TryGetValue(grouping, out var entry) && ReferenceEquals(entry.Root, root)) return entry.Snapshot;

            var options = new List<FBXUVMeshOption>();
            if (root != null)
            {
                if (grouping == FBXUVCandidateGrouping.ByRenderer) AddRendererSourceOptions(options, root);
                else AddMeshSourceOptions(options, root);
            }

            var snapshot = new Snapshot(options);
            groupedSources[grouping] = new GroupedEntry { Root = root, Snapshot = snapshot };
            return snapshot;
        }

        internal Snapshot GetTarget(
            Component layer,
            GameObject reference,
            Texture texture,
            FBXUVCandidateGrouping grouping,
            out string error)
        {
            var root = ResolveTargetRoot(layer, reference, out error);
            if (groupedTargets.TryGetValue(grouping, out var entry) &&
                ReferenceEquals(entry.Root, root) &&
                ReferenceEquals(entry.Texture, texture))
            {
                return entry.Snapshot;
            }

            Snapshot snapshot;
            if (grouping == FBXUVCandidateGrouping.ByRenderer)
            {
                var options = new List<FBXUVMeshOption>();
                if (root != null) AddRendererTargetOptions(options, root, texture);
                snapshot = new Snapshot(options);
            }
            else
            {
                snapshot = new Snapshot(CreateMeshTargetOptions(CollectTargetMeshes(root, texture)));
            }

            groupedTargets[grouping] = new GroupedEntry { Root = root, Texture = texture, Snapshot = snapshot };
            return snapshot;
        }

        internal void Invalidate()
        {
            groupedSources.Clear();
            groupedTargets.Clear();
        }

        private static GameObject ResolveTargetRoot(Component layer, GameObject reference, out string error)
        {
            var validRoot = FBXUVModelPrefabReferenceUtility.TryResolveRoot(reference, out var root, out error);
            // Recheck before consulting the cache, even if the caller holds an old texture.
            var validCanvas = FBXUVCanvasHierarchyUtility.TryFindCanvas(layer, out _, out var canvasError, out _);
            if (!validCanvas)
            {
                root = null;
                if (validRoot) error = canvasError;
            }

            return root;
        }

        private static List<FBXUVTargetMeshCandidate> CollectTargetMeshes(GameObject root, Texture texture)
        {
            return root != null
                ? FBXUVTargetMeshCollector.Collect(root, texture)
                : new List<FBXUVTargetMeshCandidate>();
        }

        private static List<FBXUVMeshOption> CreateMeshTargetOptions(List<FBXUVTargetMeshCandidate> candidates)
        {
            var options = new List<FBXUVMeshOption>();
            foreach (var candidate in candidates)
                foreach (var subMeshIndex in candidate.SubMeshIndices)
                    options.Add(new FBXUVMeshOption(SubMeshLabel(candidate.Label, subMeshIndex), candidate.Mesh, subMeshIndex));
            return options;
        }

        private static void AddMeshSourceOptions(List<FBXUVMeshOption> options, GameObject root)
        {
            foreach (var mesh in FBXUVModelPrefabReferenceUtility.CollectMeshes(root))
                for (var subMeshIndex = 0; subMeshIndex < mesh.Mesh.subMeshCount; subMeshIndex++)
                    options.Add(new FBXUVMeshOption(SubMeshLabel(mesh.Label, subMeshIndex), mesh.Mesh, subMeshIndex));
        }

        private static void AddRendererSourceOptions(List<FBXUVMeshOption> options, GameObject root)
        {
            foreach (var item in FBXUVRendererMeshUtility.Collect(root))
            {
                var path = FBXUVRendererMeshUtility.RelativePath(root.transform, item.Renderer.transform);
                for (var subMeshIndex = 0; subMeshIndex < item.Mesh.subMeshCount; subMeshIndex++)
                    options.Add(CreateRendererOption(root, item.Renderer, item.Mesh, subMeshIndex, path));
            }
        }

        private static void AddRendererTargetOptions(List<FBXUVMeshOption> options, GameObject root, Texture texture)
        {
            foreach (var item in FBXUVTargetMeshCollector.CollectRenderers(root, texture))
            {
                var path = FBXUVRendererMeshUtility.RelativePath(root.transform, item.Renderer.transform);
                // Retain the existing rejection of ambiguous saved renderer paths.
                try
                {
                    FBXUVEyeGeometry.ResolveRenderer(
                        root,
                        new FBXUVEyeRegion { rendererPath = path, region = new FBXUVTransferRegion { mesh = item.Mesh } });
                }
                catch (ArgumentException)
                {
                    continue;
                }

                foreach (var subMeshIndex in item.SubMeshIndices)
                    options.Add(CreateRendererOption(root, item.Renderer, item.Mesh, subMeshIndex, path));
            }
        }

        private static FBXUVMeshOption CreateRendererOption(
            GameObject root,
            Renderer renderer,
            Mesh mesh,
            int subMeshIndex,
            string path)
        {
            var rendererLabel = renderer is SkinnedMeshRenderer ? "Skinned" : "MeshFilter";
            var hierarchyPath = FBXUVModelPrefabReferenceUtility.HierarchyPath(root.transform, renderer.transform);
            return new FBXUVMeshOption(
                SubMeshLabel($"{hierarchyPath} ({rendererLabel})", subMeshIndex),
                mesh,
                subMeshIndex,
                path);
        }

        private static string SubMeshLabel(string label, int subMeshIndex)
        {
            return $"{label} / サブメッシュ {subMeshIndex}";
        }
    }

    internal static class FBXUVMeshSelectionField
    {
        // A saved mesh outside the candidates stays untouched until the user explicitly picks a replacement.
        internal static bool Draw(
            FBXUVMeshCandidateCache.Snapshot snapshot,
            int selectedIndex,
            string label,
            string replacementLabel,
            Mesh currentMesh,
            int currentSubMeshIndex,
            Func<string> warning,
            out FBXUVMeshOption selection)
        {
            selection = default;
            if (selectedIndex >= 0)
            {
                EditorGUI.BeginChangeCheck();
                var next = EditorGUILayout.Popup(label, selectedIndex, snapshot.Labels);
                if (!EditorGUI.EndChangeCheck()) return false;
                selection = snapshot.Options[next];
                return true;
            }

            using (new EditorGUI.DisabledScope(true))
            {
                EditorGUILayout.ObjectField("保存済みのメッシュ", currentMesh, typeof(Mesh), false);
                EditorGUILayout.IntField("保存済みのサブメッシュ", currentSubMeshIndex);
            }

            EditorGUILayout.HelpBox(warning(), MessageType.Warning);
            if (snapshot.Options.Count == 0) return false;

            var replacementIndex = EditorGUILayout.Popup(replacementLabel, 0, snapshot.ReplacementLabels);
            if (replacementIndex <= 0) return false;
            selection = snapshot.Options[replacementIndex - 1];
            return true;
        }
    }
}
