using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using net.rs64.TexTransTool.MultiLayerImage;
using UnityEditor;
using UnityEngine;

namespace GokouKotori.FBXUVTextureTransfer.Editor.NDMF
{
    internal static class FBXUVMakeupTransferValidation
    {
        internal static IReadOnlyList<FBXUVTransferValidationIssue> ValidateLayer(
            FBXUVMakeupTransferLayer layer, bool validateEnvironment = true)
        {
            return Validate(layer, validateEnvironment, null);
        }

        internal static IReadOnlyList<FBXUVTransferValidationIssue> ValidateLayerForBuild(
            FBXUVMakeupTransferLayer layer, GameObject buildAvatarRoot)
        {
            if (buildAvatarRoot == null) throw new ArgumentNullException(nameof(buildAvatarRoot));
            return Validate(layer, false, buildAvatarRoot);
        }

        private static IReadOnlyList<FBXUVTransferValidationIssue> Validate(
            FBXUVMakeupTransferLayer layer, bool environment, GameObject buildRoot)
        {
            var issues = new List<FBXUVTransferValidationIssue>();
            if (layer == null)
            {
                issues.Add(new FBXUVTransferValidationIssue("Makeup Transfer Layer がありません。", null));
                return issues;
            }
            void Add(string message, UnityEngine.Object context = null) =>
                issues.Add(new FBXUVTransferValidationIssue(message, context != null ? context : layer));

            if (environment) FBXUVTextureTransferValidation.ValidateEnvironment(issues, layer);
            if (!FBXUVAvatarRootResolver.TryResolve(layer, out var avatarRoot, out var rootError)) Add(rootError);
            if (layer.GetComponents<ExternalToolAsLayer>().Length != 1)
                Add("同じ GameObject に ExternalToolAsLayer が1つ必要です。");
            if (!layer.HasExclusiveExternalToolImplementation())
                Add("同じ GameObject の ExternalTool実装は Makeup Transfer Layer 1つだけにしてください。既存の転写Layerとは別のGameObjectに配置します。");

            Texture targetTexture = null;
            if (!FBXUVCanvasHierarchyUtility.TryFindCanvas(layer, out var canvas, out var error, out var context)) Add(error, context);
            else
            {
                targetTexture = canvas.TargetTexture == null ? null : canvas.TargetTexture.SelectTexture;
                if (targetTexture == null) Add("親 MultiLayerImageCanvas の Target Texture が未設定です。", canvas);
            }

            if (!layer.CanRender(out var reason)) Add(reason);
            var pngPath = layer.makeupTexture == null ? "" : AssetDatabase.GetAssetPath(layer.makeupTexture);
            if (!string.IsNullOrEmpty(pngPath))
            {
                if (!string.Equals(Path.GetExtension(pngPath), ".png", StringComparison.OrdinalIgnoreCase))
                    Add("Makeup Texture にはメイクだけの透過PNGを指定してください。", layer.makeupTexture);
                else if (AssetImporter.GetAtPath(pngPath) is TextureImporter importer &&
                         (importer.alphaSource == TextureImporterAlphaSource.None || !importer.DoesSourceTextureHaveAlpha()))
                    Add("Makeup Texture のalphaが利用できません。透過PNGとAlpha Sourceの設定を確認してください。", layer.makeupTexture);
            }

            var sourceValid = ResolveRoot(layer.sourceModelOrPrefab, "Source", buildRoot, out var sourceRoot, out var sourceError);
            if (!sourceValid) Add(sourceError);
            else if (!ContainsMesh(sourceRoot, layer.sourceRegion == null ? null : layer.sourceRegion.mesh))
                Add("Source の顔Meshが指定した Model / Prefab 内にありません。顔UVを選択し直してください。");
            var targetValid = ResolveRoot(layer.targetModelOrPrefab, "Target", buildRoot, out var targetRoot, out var targetError);
            if (!targetValid) Add(targetError);
            else if (targetTexture != null && !MatchesTarget(targetRoot, targetTexture, layer.targetRegion))
                Add("Target の顔Mesh / Submesh が、指定Model / Prefabで MLIC のTarget Textureを使用していません。");
            if (avatarRoot != null && targetTexture != null && !MatchesTarget(avatarRoot, targetTexture, layer.targetRegion))
                Add("アバター内に、選択した顔Mesh / Submeshと MLIC のTarget Textureを使用するRendererがありません。");
            return issues;
        }

        private static bool ResolveRoot(GameObject reference, string label, GameObject buildRoot,
            out GameObject root, out string error)
        {
            if (FBXUVModelPrefabReferenceUtility.TryResolveRootForBuild(
                reference, buildRoot, out root, out error)) return true;
            error = label + " Model / Prefab にProject内のModelまたはPrefabのメインルートを指定してください。";
            return false;
        }

        private static bool ContainsMesh(GameObject root, Mesh mesh)
        {
            return root != null && mesh != null &&
                   (root.GetComponentsInChildren<SkinnedMeshRenderer>(true).Any(r => r.sharedMesh == mesh) ||
                    root.GetComponentsInChildren<MeshFilter>(true).Any(r => r.sharedMesh == mesh));
        }

        private static bool MatchesTarget(GameObject root, Texture texture, FBXUVTransferRegion region)
        {
            return region != null && region.mesh != null && FBXUVTargetMeshCollector.Collect(root, texture)
                .Any(c => c.Mesh == region.mesh && c.SubMeshIndices.Contains(region.subMeshIndex));
        }
    }
}
