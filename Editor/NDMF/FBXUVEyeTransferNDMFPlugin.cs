using System.Linq;
using nadena.dev.ndmf;
using UnityEngine;

[assembly: ExportsPlugin(typeof(GokouKotori.FBXUVTextureTransfer.Editor.NDMF.FBXUVEyeTransferNDMFPlugin))]

namespace GokouKotori.FBXUVTextureTransfer.Editor.NDMF
{
    [RunsOnPlatforms(WellKnownPlatforms.VRChatAvatar30)]
    internal sealed class FBXUVEyeTransferNDMFPlugin : Plugin<FBXUVEyeTransferNDMFPlugin>
    {
        public override string QualifiedName => "com.gokoukotori.fbx-uv-texture-transfer.eye";
        public override string DisplayName => "FBX UV Eye Texture Transfer";
        protected override void Configure()
        {
            InPhase(BuildPhase.Resolving).BeforePlugin("net.rs64.tex-trans-tool").Run("アイ転写の設定を検証", context =>
            {
                var layers = context.AvatarRootObject.GetComponentsInChildren<FBXUVEyeTextureTransferLayer>(true)
                    .Where(l => l.IsEnabledInHierarchy).ToArray();
                var environment = true;
                foreach (var layer in layers)
                {
                    foreach (var issue in FBXUVEyeTransferValidation.ValidateLayer(layer, environment, context.AvatarRootObject))
                        using (ErrorReport.WithContextObject(issue.ContextObject))
                            ErrorReport.ReportError(new FBXUVTransferValidationError(issue.Message));
                    environment = false;
                }
            });
            InPhase(BuildPhase.Optimizing).AfterPlugin("net.rs64.tex-trans-tool").Run("アイ転写コンポーネントを除去", context =>
            {
                foreach (var layer in context.AvatarRootObject.GetComponentsInChildren<FBXUVEyeTextureTransferLayer>(true))
                    Object.DestroyImmediate(layer);
            });
        }
    }
}
