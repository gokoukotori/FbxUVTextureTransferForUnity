using System.Collections.Generic;
using nadena.dev.ndmf;
using UnityEngine;

[assembly: ExportsPlugin(typeof(GokouKotori.FBXUVTextureTransfer.Editor.NDMF.FBXUVMakeupTransferNDMFPlugin))]

namespace GokouKotori.FBXUVTextureTransfer.Editor.NDMF
{
    [RunsOnPlatforms(WellKnownPlatforms.VRChatAvatar30)]
    internal sealed class FBXUVMakeupTransferNDMFPlugin : FBXUVLayerNDMFPlugin<FBXUVMakeupTransferNDMFPlugin, FBXUVMakeupTransferLayer>
    {
        public override string QualifiedName => "com.gokoukotori.fbx-uv-texture-transfer.makeup";
        public override string DisplayName => "FBX UV Makeup Transfer";
        protected override string ValidationPassName => "メイク転写の設定を検証";
        protected override string CleanupPassName => "メイク転写コンポーネントを除去";

        protected override IEnumerable<FBXUVTransferValidationIssue> ValidateForBuild(
            FBXUVMakeupTransferLayer layer, GameObject avatarRoot) =>
            FBXUVMakeupTransferValidation.ValidateLayerForBuild(layer, avatarRoot);
    }
}
