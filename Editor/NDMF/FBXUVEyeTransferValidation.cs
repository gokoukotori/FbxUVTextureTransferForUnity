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
            var issues = new List<FBXUVTransferValidationIssue>();
            void Add(string text) => issues.Add(new FBXUVTransferValidationIssue(text, layer));
            if (layer == null) { Add("Eye Transfer Layerがありません。"); return issues; }
            if (environment) FBXUVTextureTransferValidation.ValidateEnvironment(issues, layer);
            var placement = FBXUVLayerValidation.ValidatePlacement(layer, "FBX UV Eye Texture Transfer Layer", issues);
            var avatar = placement.AvatarRoot;
            var texture = placement.TargetTexture;
            var sourceValid = FBXUVLayerValidation.TryResolveModelRoot(layer.sourceModelOrPrefab, "Source", buildRoot, layer, issues, out var source);
            var targetValid = FBXUVLayerValidation.TryResolveModelRoot(layer.targetModelOrPrefab, "Target", buildRoot, layer, issues, out var target);
            // Build clones may remap prefab self references. Resolve asset roots before reading base geometry.
            if (buildRoot != null && sourceValid && targetValid)
            { layer.sourceModelOrPrefab = source; layer.targetModelOrPrefab = target; }
            if (!layer.CanRender(out var reason)) Add(reason);
            foreach (var eye in layer.eyes ?? new List<FBXUVEyeBinding>())
            {
                if (eye == null || !eye.enabled || eye.target?.region == null) continue;
                if (targetValid && texture != null && !MatchesSelectedRenderer(target, texture, eye.target))
                    Add(eye.name + ": 転送先のRenderer / SubmeshがCanvasのTargetTextureを使用していません。");
                if (avatar != null && texture != null &&
                    !FBXUVLayerValidation.UsesTargetTexture(avatar, texture, eye.target.region.mesh, eye.target.region.subMeshIndex))
                    Add(eye.name + ": アバター内に、選択したMesh / SubmeshとCanvasのTargetTextureを使用するRendererがありません。");
            }
            return issues;
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
