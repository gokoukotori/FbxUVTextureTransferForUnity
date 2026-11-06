using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf;
using UnityEngine;

namespace GokouKotori.FBXUVTextureTransfer.Editor.NDMF
{
    // Build wiring shared by the transfer layers: validate the enabled layers before TexTransTool runs, then remove
    // every layer after TexTransTool has purged its ExternalToolAsLayer wrappers (they require the layer as the
    // interface implementation). The transferred images are evaluated during Transforming, so removing the layers
    // in Optimizing does not change the output.
    internal abstract class FBXUVLayerNDMFPlugin<TPlugin, TLayer> : Plugin<TPlugin>
        where TPlugin : Plugin<TPlugin>, new()
        where TLayer : FBXUVTransferLayerBase
    {
        private const string TexTransToolPlugin = "net.rs64.tex-trans-tool";

        protected abstract string ValidationPassName { get; }
        protected abstract string CleanupPassName { get; }

        // Per-layer build validation without the environment checks, which run once per build.
        protected abstract IEnumerable<FBXUVTransferValidationIssue> ValidateForBuild(TLayer layer, GameObject avatarRoot);

        protected sealed override void Configure()
        {
            InPhase(BuildPhase.Resolving).BeforePlugin(TexTransToolPlugin).Run(ValidationPassName, Validate);
            InPhase(BuildPhase.Optimizing).AfterPlugin(TexTransToolPlugin).Run(CleanupPassName, RemoveLayers);
        }

        private void Validate(BuildContext context)
        {
            var layers = context.AvatarRootObject.GetComponentsInChildren<TLayer>(true)
                .Where(layer => layer.IsEnabledInHierarchy)
                .ToArray();
            if (layers.Length == 0) return;

            // Environment errors are reported once, not per layer.
            var environmentIssues = new List<FBXUVTransferValidationIssue>();
            FBXUVEnvironmentValidation.Validate(environmentIssues, layers[0]);
            Report(environmentIssues);
            foreach (var layer in layers)
            {
                Report(ValidateForBuild(layer, context.AvatarRootObject));
            }
        }

        private static void RemoveLayers(BuildContext context)
        {
            foreach (var layer in context.AvatarRootObject.GetComponentsInChildren<TLayer>(true))
            {
                Object.DestroyImmediate(layer);
            }
        }

        private static void Report(IEnumerable<FBXUVTransferValidationIssue> issues)
        {
            foreach (var issue in issues)
            {
                using (ErrorReport.WithContextObject(issue.ContextObject))
                {
                    ErrorReport.ReportError(new FBXUVTransferValidationError(issue.Message));
                }
            }
        }
    }
}
