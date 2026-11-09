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
            if (!FBXUVLayerValidation.TryBegin(layer, "FBX UV Makeup Transfer Layer がありません。", "FBX UV Makeup Transfer Layer",
                    environment, buildRoot, useResolvedRootsForBuild: false, out var checks))
                return checks.Issues;
            var avatarRoot = checks.AvatarRoot;
            var targetTexture = checks.TargetTexture;

            if (!layer.CanRender(out var reason)) checks.Add(reason);
            var pngPath = layer.makeupTexture == null ? "" : AssetDatabase.GetAssetPath(layer.makeupTexture);
            if (!string.IsNullOrEmpty(pngPath))
            {
                if (!string.Equals(Path.GetExtension(pngPath), ".png", StringComparison.OrdinalIgnoreCase))
                    checks.Add("メイクの透過PNGには、メイクだけを描いたPNGファイルを指定してください。", layer.makeupTexture);
                else if (AssetImporter.GetAtPath(pngPath) is TextureImporter importer &&
                         (importer.alphaSource == TextureImporterAlphaSource.None || !importer.DoesSourceTextureHaveAlpha()))
                    checks.Add("メイクの透過PNGのアルファが利用できません。透過PNGとAlpha Sourceの設定を確認してください。", layer.makeupTexture);
            }

            if (checks.SourceValid && !FBXUVModelPrefabReferenceUtility.ContainsMesh(checks.SourceRoot, layer.sourceRegion == null ? null : layer.sourceRegion.mesh))
                checks.Add("転送元の顔のメッシュが指定した Model / Prefab 内にありません。" + FBXUVEditorNames.MakeupTransferEditor + "で顔のUV島を選び直してください。");
            if (checks.TargetValid && targetTexture != null && !MatchesTarget(checks.TargetRoot, targetTexture, layer.targetRegion))
                checks.Add("転送先の顔のメッシュ / サブメッシュが、指定した Model / Prefab でCanvasの対象テクスチャを使用していません。");
            if (avatarRoot != null && targetTexture != null && !MatchesTarget(avatarRoot, targetTexture, layer.targetRegion))
                checks.Add("アバター内に、選択した顔のメッシュ / サブメッシュとCanvasの対象テクスチャを使用するRendererがありません。");
            return checks.Issues;
        }

        private static bool MatchesTarget(GameObject root, Texture texture, FBXUVTransferRegion region)
        {
            return region != null && FBXUVLayerValidation.UsesTargetTexture(root, texture, region.mesh, region.subMeshIndex);
        }
    }
}
