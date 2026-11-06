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

        // The issues of one layer and what the common checks found. Each validator adds its own checks to it.
        internal sealed class Context
        {
            private readonly Object layer;

            internal Context(Object layer)
            {
                this.layer = layer;
            }

            internal List<FBXUVTransferValidationIssue> Issues { get; } = new List<FBXUVTransferValidationIssue>();
            internal GameObject AvatarRoot { get; set; }
            internal Texture TargetTexture { get; set; }
            internal bool SourceValid { get; set; }
            internal GameObject SourceRoot { get; set; }
            internal bool TargetValid { get; set; }
            internal GameObject TargetRoot { get; set; }

            internal void Add(string message) => Add(message, layer);

            internal void Add(string message, Object contextObject)
            {
                Issues.Add(new FBXUVTransferValidationIssue(message, contextObject != null ? contextObject : layer));
            }
        }

        // The checks every transfer layer starts with, in this order: the environment (unless the build checks it once),
        // the placement, then the Source and Target Model / Prefab roots. False when there is no layer; Issues then holds
        // missingLayerMessage.
        //
        // useResolvedRootsForBuild: during a build, point the layer at the resolved Model / Prefab asset roots. A build
        // clone may have remapped a prefab self reference to the clone itself. The eye and body layers read the base
        // geometry of these roots when they render, so they need the asset roots before CanRender (true). The texture and
        // makeup layers render from their saved regions and keep the references as they are (false).
        internal static bool TryBegin(
            FBXUVTransferLayerBase layer,
            string missingLayerMessage,
            string layerName,
            bool environment,
            GameObject buildRoot,
            bool useResolvedRootsForBuild,
            out Context checks)
        {
            checks = new Context(layer);
            if (layer == null)
            {
                checks.Issues.Add(new FBXUVTransferValidationIssue(missingLayerMessage, null));
                return false;
            }

            if (environment) FBXUVEnvironmentValidation.Validate(checks.Issues, layer);
            var placement = ValidatePlacement(layer, layerName, checks.Issues);
            checks.AvatarRoot = placement.AvatarRoot;
            checks.TargetTexture = placement.TargetTexture;
            checks.SourceValid = TryResolveModelRoot(layer.SourceModelOrPrefab, "Source", buildRoot, layer, checks.Issues, out var sourceRoot);
            checks.SourceRoot = sourceRoot;
            checks.TargetValid = TryResolveModelRoot(layer.TargetModelOrPrefab, "Target", buildRoot, layer, checks.Issues, out var targetRoot);
            checks.TargetRoot = targetRoot;
            if (useResolvedRootsForBuild && buildRoot != null && checks.SourceValid && checks.TargetValid)
            {
                layer.SourceModelOrPrefab = sourceRoot;
                layer.TargetModelOrPrefab = targetRoot;
            }

            return true;
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
