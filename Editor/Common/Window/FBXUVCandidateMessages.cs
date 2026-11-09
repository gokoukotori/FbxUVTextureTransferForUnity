using UnityEngine;

namespace GokouKotori.FBXUVTextureTransfer.Editor
{
    internal static class FBXUVCandidateMessages
    {
        internal const string NoIslands = "このUVチャンネルに選択できるUV島がありません。";

        internal static string Source(GameObject sourceRoot, int optionCount, bool hasCurrentMesh)
        {
            if (!FBXUVModelPrefabReferenceUtility.TryResolveRoot(sourceRoot, out _, out var rootError))
            {
                return $"転送元 Model / Prefab: {rootError} 保存済みの転送元の選択は保持しています。";
            }

            if (optionCount == 0)
            {
                return "転送元 Model / Prefab配下にメッシュがありません。保存済みの転送元の選択は保持しています。";
            }

            return !hasCurrentMesh
                ? "転送元にメッシュが保存されていません。候補から選択してください。"
                : "保存済みの転送元メッシュは現在の転送元 Model / Prefab配下にありません。保存済みの値は保持しています。置き換える場合だけ候補から選択してください。";
        }

        internal static string Target(FBXUVTransferLayerBase layer, Texture targetTexture, int optionCount, bool hasCurrentMesh)
        {
            if (!FBXUVCanvasHierarchyUtility.TryFindCanvas(layer, out _, out var error, out _))
            {
                return $"{error} 保存済みの転送先の選択は保持しています。";
            }

            if (targetTexture == null)
            {
                return "Canvasの対象テクスチャが未設定です。親 MultiLayerImageCanvas の Target Texture を設定してください。保存済みの転送先の選択は保持しています。";
            }

            if (!FBXUVModelPrefabReferenceUtility.TryResolveRoot(layer.TargetModelOrPrefab, out _, out var rootError))
            {
                return $"転送先 Model / Prefab: {rootError} 保存済みの転送先の選択は保持しています。";
            }

            if (optionCount == 0)
            {
                return "Canvasの対象テクスチャを参照するMaterialを持つRendererが転送先 Model / Prefab 配下にありません。保存済みの転送先の選択は保持しています。";
            }

            return !hasCurrentMesh
                ? "転送先にメッシュ / サブメッシュが保存されていません。候補から選択してください。"
                : "保存済みの転送先メッシュ / サブメッシュは、現在のCanvasの対象テクスチャを参照するRendererで使われていません。保存済みの値は保持しています。置き換える場合だけ候補から選択してください。";
        }
    }
}
