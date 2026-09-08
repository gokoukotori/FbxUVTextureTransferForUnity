using System.Linq;
using nadena.dev.ndmf;
using UnityEngine;

[assembly: ExportsPlugin(typeof(GokouKotori.FBXUVTextureTransfer.Editor.NDMF.FBXUVMakeupTransferNDMFPlugin))]

namespace GokouKotori.FBXUVTextureTransfer.Editor.NDMF
{
    [RunsOnPlatforms(WellKnownPlatforms.VRChatAvatar30)]
    internal sealed class FBXUVMakeupTransferNDMFPlugin : Plugin<FBXUVMakeupTransferNDMFPlugin>
    {
        public override string QualifiedName => "com.gokoukotori.fbx-uv-texture-transfer.makeup";
        public override string DisplayName => "FBX UV Makeup Transfer";

        protected override void Configure()
        {
            InPhase(BuildPhase.Resolving).BeforePlugin("net.rs64.tex-trans-tool").Run("メイク転写の設定を検証", context =>
            {
                var layers = context.AvatarRootObject.GetComponentsInChildren<FBXUVMakeupTransferLayer>(true)
                    .Where(l => l.IsEnabledInHierarchy).ToArray();
                if (layers.Length == 0) return;
                var environmentIssues = new System.Collections.Generic.List<FBXUVTransferValidationIssue>();
                FBXUVTextureTransferValidation.ValidateEnvironment(environmentIssues, layers[0]);
                foreach (var issue in environmentIssues) Report(issue);
                foreach (var layer in layers)
                    foreach (var issue in FBXUVMakeupTransferValidation.ValidateLayerForBuild(layer, context.AvatarRootObject))
                        Report(issue);
            });
            // The wrapper must be purged by TTT before removing the interface implementation.
            InPhase(BuildPhase.Optimizing).AfterPlugin("net.rs64.tex-trans-tool").Run("メイク転写コンポーネントを除去", context =>
            {
                foreach (var layer in context.AvatarRootObject.GetComponentsInChildren<FBXUVMakeupTransferLayer>(true))
                    Object.DestroyImmediate(layer);
            });
        }

        private static void Report(FBXUVTransferValidationIssue issue)
        {
            using (ErrorReport.WithContextObject(issue.ContextObject))
                ErrorReport.ReportError(new FBXUVTransferValidationError(issue.Message));
        }
    }
}
