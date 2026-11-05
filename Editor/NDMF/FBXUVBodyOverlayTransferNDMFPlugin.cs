using System.Collections.Generic;
using nadena.dev.ndmf;
using UnityEngine;

[assembly: ExportsPlugin(typeof(GokouKotori.FBXUVTextureTransfer.Editor.NDMF.FBXUVBodyOverlayTransferNDMFPlugin))]

namespace GokouKotori.FBXUVTextureTransfer.Editor.NDMF
{
    [RunsOnPlatforms(WellKnownPlatforms.VRChatAvatar30)]
    internal sealed class FBXUVBodyOverlayTransferNDMFPlugin : FBXUVLayerNDMFPlugin<FBXUVBodyOverlayTransferNDMFPlugin, FBXUVBodyOverlayTransferLayer>
    {
        public override string QualifiedName => "com.gokoukotori.fbx-uv-texture-transfer.body";
        public override string DisplayName => "FBX UV Body Overlay Transfer";
        protected override string ValidationPassName => "ボディ転送の設定を検証";
        protected override string CleanupPassName => "ボディ転送コンポーネントを除去";

        protected override IEnumerable<FBXUVTransferValidationIssue> ValidateForBuild(
            FBXUVBodyOverlayTransferLayer layer, GameObject avatarRoot) =>
            FBXUVBodyOverlayTransferValidation.ValidateLayer(layer, false, avatarRoot);
    }
}
