using System.Collections.Generic;
using System.Linq;
using net.rs64.TexTransTool.MultiLayerImage;
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
            if (!FBXUVAvatarRootResolver.TryResolve(layer, out var avatar, out var avatarError)) Add(avatarError);
            if (layer.GetComponents<ExternalToolAsLayer>().Length != 1 || !layer.HasExclusiveExternalToolImplementation())
                Add("同じGameObjectにはEye Transfer LayerとExternalToolAsLayerを1つずつ配置してください。他の転写Layerは別のGameObjectに配置します。");
            Texture texture = null;
            if (!FBXUVCanvasHierarchyUtility.TryFindCanvas(layer, out var canvas, out var canvasError, out _)) Add(canvasError);
            else
            {
                texture = canvas.TargetTexture?.SelectTexture;
                if (texture == null) Add("親MultiLayerImageCanvasのTarget Textureが未設定です。");
            }
            var sourceValid = FBXUVModelPrefabReferenceUtility.TryResolveRootForBuild(layer.sourceModelOrPrefab, buildRoot, out var source, out _);
            var targetValid = FBXUVModelPrefabReferenceUtility.TryResolveRootForBuild(layer.targetModelOrPrefab, buildRoot, out var target, out _);
            if (!sourceValid) Add("転送元にはProject内のModel / Prefabのメインルートを指定してください。");
            if (!targetValid) Add("転送先にはProject内のModel / Prefabのメインルートを指定してください。");
            // Build clones may remap prefab self references. Resolve asset roots before reading base geometry.
            if (buildRoot != null && sourceValid && targetValid)
            { layer.sourceModelOrPrefab = source; layer.targetModelOrPrefab = target; }
            if (!layer.CanRender(out var reason)) Add(reason);
            foreach (var eye in layer.eyes ?? new List<FBXUVEyeBinding>())
            {
                if (eye == null || !eye.enabled || eye.target?.region == null) continue;
                if (targetValid && texture != null && !MatchesSelectedRenderer(target, texture, eye.target))
                    Add(eye.name + ": 転送先のRenderer / SubmeshがCanvasのTarget Textureを使用していません。");
                if (avatar != null && texture != null && !FBXUVTargetMeshCollector.Collect(avatar, texture)
                        .Any(c => c.Mesh == eye.target.region.mesh && c.SubMeshIndices.Contains(eye.target.region.subMeshIndex)))
                    Add(eye.name + ": アバター内に、選択したMesh / SubmeshとCanvasのTarget Textureを使用するRendererがありません。");
            }
            return issues;
        }

        internal static bool MatchesSelectedRenderer(GameObject root, Texture texture, FBXUVEyeRegion selection)
        {
            try
            {
                var renderer = FBXUVEyeGeometry.ResolveRenderer(root, selection);
                var materials = renderer.sharedMaterials;
                var mesh = selection.region.mesh;
                var cache = new Dictionary<Material, bool>();
                for (var i = 0; i < materials.Length; i++)
                    if (Mathf.Min(i, mesh.subMeshCount - 1) == selection.region.subMeshIndex
                        && FBXUVTargetMeshCollector.UsesTexture(materials[i], texture, cache)) return true;
            }
            catch (System.ArgumentException) { }
            return false;
        }
    }
}
