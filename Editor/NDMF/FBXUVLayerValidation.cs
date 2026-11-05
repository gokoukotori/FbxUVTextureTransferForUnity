using System.Collections.Generic;
using System.Linq;
using net.rs64.TexTransTool.MultiLayerImage;
using UnityEngine;

namespace GokouKotori.FBXUVTextureTransfer.Editor.NDMF
{
    // Checks every transfer layer shares: avatar root, ExternalToolAsLayer wrapper, parent canvas and its
    // TargetTexture, Model / Prefab roots, and whether a selected target mesh uses the canvas texture.
    internal static class FBXUVLayerValidation
    {
        internal struct Placement
        {
            internal GameObject AvatarRoot;
            internal MultiLayerImageCanvas Canvas;
            internal Texture TargetTexture;
        }

        internal static Placement ValidatePlacement(
            MonoBehaviour layer,
            string layerName,
            ICollection<FBXUVTransferValidationIssue> issues)
        {
            var placement = new Placement();
            if (!FBXUVAvatarRootResolver.TryResolve(layer, out placement.AvatarRoot, out var avatarRootError))
            {
                issues.Add(new FBXUVTransferValidationIssue(avatarRootError, layer));
            }

            if (layer.GetComponents<ExternalToolAsLayer>().Length != 1)
            {
                issues.Add(new FBXUVTransferValidationIssue("同じ GameObject に ExternalToolAsLayer がちょうど1つ必要です。", layer));
            }

            var implementations = layer.GetComponents<MonoBehaviour>()
                .Where(component => component is IExternalToolCanBehaveAsLayer)
                .ToArray();
            if (implementations.Length != 1 || !ReferenceEquals(implementations[0], layer))
            {
                issues.Add(new FBXUVTransferValidationIssue(
                    $"同じ GameObject の ExternalTool実装は {layerName} 1つだけにしてください。転送レイヤーは機能ごとに別の GameObject に配置します。",
                    layer));
            }

            if (!FBXUVCanvasHierarchyUtility.TryFindCanvas(layer, out placement.Canvas, out var canvasError, out var context))
            {
                issues.Add(new FBXUVTransferValidationIssue(canvasError, context));
                return placement;
            }

            placement.TargetTexture = placement.Canvas.TargetTexture == null ? null : placement.Canvas.TargetTexture.SelectTexture;
            if (placement.TargetTexture == null)
            {
                issues.Add(new FBXUVTransferValidationIssue("親 MultiLayerImageCanvas の TargetTexture が未設定です。", placement.Canvas));
            }

            return placement;
        }

        // label is "Source" or "Target".
        internal static bool TryResolveModelRoot(
            GameObject reference,
            string label,
            GameObject buildAvatarRoot,
            Object contextObject,
            ICollection<FBXUVTransferValidationIssue> issues,
            out GameObject root)
        {
            root = null;
            if (reference == null)
            {
                issues.Add(new FBXUVTransferValidationIssue(
                    $"{label} Model/Prefab が未設定です。コンポーネントでProject内のModelまたはPrefabのメインルートを指定してください。",
                    contextObject));
                return false;
            }

            if (FBXUVModelPrefabReferenceUtility.TryResolveRootForBuild(reference, buildAvatarRoot, out root, out var error))
            {
                return true;
            }

            issues.Add(new FBXUVTransferValidationIssue($"{label} Model/Prefab が無効です: {error}", reference));
            return false;
        }

        // True when a Renderer under root draws the mesh / submesh with a material that uses texture.
        internal static bool UsesTargetTexture(GameObject root, Texture texture, Mesh mesh, int subMeshIndex)
        {
            return root != null && texture != null && mesh != null && FBXUVTargetMeshCollector.Collect(root, texture)
                .Any(candidate => candidate.Mesh == mesh && candidate.SubMeshIndices.Contains(subMeshIndex));
        }
    }
}
