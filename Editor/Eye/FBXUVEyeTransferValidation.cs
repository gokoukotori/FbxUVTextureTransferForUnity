using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace GokouKotori.FBXUVTextureTransfer.Editor.NDMF
{
    internal static class FBXUVEyeTransferValidation
    {
        internal static IReadOnlyList<FBXUVTransferValidationIssue> ValidateLayer(
            FBXUVEyeTextureTransferLayer layer, bool environment = true, GameObject buildRoot = null)
        {
            if (!FBXUVLayerValidation.TryBegin(layer, "FBX UV Eye Texture Transfer Layer がありません。", "FBX UV Eye Texture Transfer Layer",
                    environment, buildRoot, useResolvedRootsForBuild: true, out var checks))
                return checks.Issues;
            var avatar = checks.AvatarRoot;
            var texture = checks.TargetTexture;
            if (!layer.CanRender(out var reason)) checks.Add(reason);
            foreach (var eye in layer.eyes ?? new List<FBXUVEyeBinding>())
            {
                if (eye == null || !eye.enabled || eye.target?.region == null) continue;
                if (checks.TargetValid && texture != null && !MatchesSelectedRenderer(checks.TargetRoot, texture, eye.target))
                    checks.Add(eye.name + ": 転送先のRenderer / サブメッシュがCanvasの対象テクスチャを使用していません。");
                if (avatar != null && texture != null &&
                    !FBXUVLayerValidation.UsesTargetTexture(avatar, texture, eye.target.region.mesh, eye.target.region.subMeshIndex))
                    checks.Add(eye.name + ": アバター内に、選択したメッシュ / サブメッシュとCanvasの対象テクスチャを使用するRendererがありません。");
            }
            return checks.Issues;
        }

        internal static bool MatchesSelectedRenderer(GameObject root, Texture texture, FBXUVEyeRegion selection)
        {
            try
            {
                var renderer = FBXUVEyeGeometry.ResolveRenderer(root, selection);
                return FBXUVTargetMeshCollector.CollectRenderers(root, texture)
                    .Any(candidate => candidate.Renderer == renderer
                        && candidate.SubMeshIndices.Contains(selection.region.subMeshIndex));
            }
            catch (System.ArgumentException) { }
            return false;
        }
    }
}
