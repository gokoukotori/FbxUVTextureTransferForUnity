using System;
using System.Collections.Generic;
using System.IO;
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
            var placement = FBXUVLayerValidation.ValidatePlacement(layer, "FBX UV Makeup Transfer Layer", issues);
            var avatarRoot = placement.AvatarRoot;
            var targetTexture = placement.TargetTexture;

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

            var sourceValid = FBXUVLayerValidation.TryResolveModelRoot(layer.sourceModelOrPrefab, "Source", buildRoot, layer, issues, out var sourceRoot);
            if (sourceValid && !FBXUVModelPrefabReferenceUtility.ContainsMesh(sourceRoot, layer.sourceRegion == null ? null : layer.sourceRegion.mesh))
                Add("Source の顔Meshが指定した Model / Prefab 内にありません。顔UVを選択し直してください。");
            var targetValid = FBXUVLayerValidation.TryResolveModelRoot(layer.targetModelOrPrefab, "Target", buildRoot, layer, issues, out var targetRoot);
            if (targetValid && targetTexture != null && !MatchesTarget(targetRoot, targetTexture, layer.targetRegion))
                Add("Target の顔Mesh / Submesh が、指定Model / Prefabで MLIC のTargetTextureを使用していません。");
            if (avatarRoot != null && targetTexture != null && !MatchesTarget(avatarRoot, targetTexture, layer.targetRegion))
                Add("アバター内に、選択した顔Mesh / Submeshと MLIC のTargetTextureを使用するRendererがありません。");
            return issues;
        }

        private static bool MatchesTarget(GameObject root, Texture texture, FBXUVTransferRegion region)
        {
            return region != null && FBXUVLayerValidation.UsesTargetTexture(root, texture, region.mesh, region.subMeshIndex);
        }
    }
}
