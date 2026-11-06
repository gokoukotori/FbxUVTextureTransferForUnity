using System.Collections.Generic;
using nadena.dev.ndmf;
using UnityEngine;

[assembly: ExportsPlugin(typeof(GokouKotori.FBXUVTextureTransfer.Editor.NDMF.FBXUVEyeTransferNDMFPlugin))]

namespace GokouKotori.FBXUVTextureTransfer.Editor.NDMF
{
    [RunsOnPlatforms(WellKnownPlatforms.VRChatAvatar30)]
    internal sealed class FBXUVEyeTransferNDMFPlugin : FBXUVLayerNDMFPlugin<FBXUVEyeTransferNDMFPlugin, FBXUVEyeTextureTransferLayer>
    {
        public override string QualifiedName => "com.gokoukotori.fbx-uv-texture-transfer.eye";
        public override string DisplayName => "FBX UV Eye Texture Transfer";
        protected override string ValidationPassName => "アイ転写の設定を検証";
        protected override string CleanupPassName => "アイ転写コンポーネントを除去";

        protected override IEnumerable<FBXUVTransferValidationIssue> ValidateForBuild(
            FBXUVEyeTextureTransferLayer layer, GameObject avatarRoot) =>
            FBXUVEyeTransferValidation.ValidateLayer(layer, false, avatarRoot);
    }
}
