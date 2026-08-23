using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace GokouKotori.FBXUVTextureTransfer.Editor
{
    internal static class FBXUVModelPrefabReferenceUtility
    {
        internal static bool TryResolveTargetRoot(
            FBXUVTextureTransferLayer layer,
            out GameObject targetRoot,
            out string error)
        {
            targetRoot = null;
            if (!FBXUVAvatarRootResolver.TryResolve(layer, out var avatarRoot, out var avatarError))
            {
                error = $"親Avatar Rootを解決できません: {avatarError}";
                return false;
            }

            var assetPath = ResolvePrefabAssetPath(avatarRoot);
            if (string.IsNullOrEmpty(assetPath))
            {
                error = "親Avatar RootにPrefab接続がありません。Target Model / Prefabを手動で指定してください。";
                return false;
            }

            var mainRoot = AssetDatabase.LoadMainAssetAtPath(assetPath) as GameObject;
            if (!TryValidateRoot(mainRoot, out var rootError))
            {
                error = $"親Avatar RootのPrefabをTargetに使用できません: {rootError}";
                return false;
            }

            targetRoot = mainRoot;
            error = string.Empty;
            return true;
        }

        internal static bool TryPopulateTargetRoot(
            FBXUVTextureTransferLayer layer,
            out bool changed,
            out string error)
        {
            changed = false;
            if (layer == null)
            {
                error = "FBX UV Texture Transfer Layerがありません。";
                return false;
            }

            if (layer.TargetModelOrPrefab != null)
            {
                if (TryResolveRoot(layer.TargetModelOrPrefab, out _, out error))
                {
                    error = string.Empty;
                    return true;
                }

                return false;
            }

            if (!TryResolveTargetRoot(layer, out var targetRoot, out error)) return false;

            return AssignTargetRoot(layer, targetRoot, out changed);
        }

        private static bool AssignTargetRoot(
            FBXUVTextureTransferLayer layer,
            GameObject targetRoot,
            out bool changed)
        {
            var serializedLayer = new SerializedObject(layer);
            serializedLayer.Update();
            serializedLayer.FindProperty("targetModelOrPrefab").objectReferenceValue = targetRoot;
            changed = serializedLayer.ApplyModifiedProperties();
            if (changed)
            {
                PrefabUtility.RecordPrefabInstancePropertyModifications(layer);
                EditorUtility.SetDirty(layer);
            }

            return true;
        }

        internal static bool TryValidateRoot(GameObject root, out string error)
        {
            if (root == null)
            {
                error = "Model / Prefabが未設定です。";
                return false;
            }

            if (!EditorUtility.IsPersistent(root) || !AssetDatabase.Contains(root))
            {
                error = "Project内のModelまたはPrefabアセットを指定してください。Scene Objectは指定できません。";
                return false;
            }

            if (!AssetDatabase.IsMainAsset(root))
            {
                error = "ModelまたはPrefabアセットのmain rootを指定してください。子GameObjectは指定できません。";
                return false;
            }

            var assetType = PrefabUtility.GetPrefabAssetType(root);
            if (assetType == PrefabAssetType.Regular ||
                assetType == PrefabAssetType.Model ||
                assetType == PrefabAssetType.Variant)
            {
                error = string.Empty;
                return true;
            }

            error = "通常Prefab、Model Prefab、またはPrefab Variantのmain rootを指定してください。";
            return false;
        }

        internal static bool TryResolveRoot(
            GameObject reference,
            out GameObject root,
            out string error)
        {
            root = null;
            if (TryValidateRoot(reference, out error))
            {
                root = reference;
                return true;
            }

            if (reference == null || EditorUtility.IsPersistent(reference) || AssetDatabase.Contains(reference))
            {
                return false;
            }

            // Unity remaps a Prefab asset's self-reference to its Scene instance root.
            // Only canonicalize that exact root; arbitrary Scene objects and Prefab children remain invalid.
            var instanceRoot = PrefabUtility.GetNearestPrefabInstanceRoot(reference);
            if (instanceRoot != reference) return false;

            var assetPath = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(reference);
            var assetRoot = string.IsNullOrEmpty(assetPath)
                ? null
                : AssetDatabase.LoadMainAssetAtPath(assetPath) as GameObject;
            if (!TryValidateRoot(assetRoot, out error)) return false;

            root = assetRoot;
            return true;
        }

        internal static List<(string Label, Mesh Mesh)> CollectMeshes(GameObject root)
        {
            var result = new List<(string Label, Mesh Mesh)>();
            if (root == null) return result;

            var seen = new HashSet<Mesh>();
            foreach (var renderer in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (renderer.sharedMesh != null && seen.Add(renderer.sharedMesh))
                {
                    result.Add(($"{HierarchyPath(root.transform, renderer.transform)} (Skinned)", renderer.sharedMesh));
                }
            }

            foreach (var filter in root.GetComponentsInChildren<MeshFilter>(true))
            {
                if (filter.sharedMesh != null && seen.Add(filter.sharedMesh))
                {
                    result.Add(($"{HierarchyPath(root.transform, filter.transform)} (MeshFilter)", filter.sharedMesh));
                }
            }

            return result;
        }

        internal static bool ContainsMesh(GameObject root, Mesh mesh)
        {
            if (root == null || mesh == null) return false;
            var meshes = CollectMeshes(root);
            for (var index = 0; index < meshes.Count; index++)
            {
                if (meshes[index].Mesh == mesh) return true;
            }

            return false;
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

        private static string ResolvePrefabAssetPath(GameObject avatarRoot)
        {
            if (avatarRoot == null) return string.Empty;
            if (EditorUtility.IsPersistent(avatarRoot)) return AssetDatabase.GetAssetPath(avatarRoot);

            var prefabStage = PrefabStageUtility.GetPrefabStage(avatarRoot);
            if (prefabStage != null) return prefabStage.assetPath;

            return PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(avatarRoot);
        }
    }
}
