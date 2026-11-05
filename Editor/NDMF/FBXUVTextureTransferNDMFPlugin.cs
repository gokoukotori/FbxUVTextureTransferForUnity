using System.Collections.Generic;
using nadena.dev.ndmf;
using UnityEngine;

[assembly: ExportsPlugin(typeof(GokouKotori.FBXUVTextureTransfer.Editor.NDMF.FBXUVTextureTransferNDMFPlugin))]

namespace GokouKotori.FBXUVTextureTransfer.Editor.NDMF
{
    [RunsOnPlatforms(WellKnownPlatforms.VRChatAvatar30)]
    internal sealed class FBXUVTextureTransferNDMFPlugin : FBXUVLayerNDMFPlugin<FBXUVTextureTransferNDMFPlugin, FBXUVTextureTransferLayer>
    {
        public override string QualifiedName => "com.gokoukotori.fbx-uv-texture-transfer";
        public override string DisplayName => "FBX UV Texture Transfer";
        protected override string ValidationPassName => "FBX UV Texture Transfer の設定を検証";
        protected override string CleanupPassName => "FBX UV Texture Transfer のビルド用コンポーネントを除去";

        protected override IEnumerable<FBXUVTransferValidationIssue> ValidateForBuild(
            FBXUVTextureTransferLayer layer, GameObject avatarRoot) =>
            FBXUVTextureTransferValidation.ValidateLayerForBuild(layer, avatarRoot);
    }
}
