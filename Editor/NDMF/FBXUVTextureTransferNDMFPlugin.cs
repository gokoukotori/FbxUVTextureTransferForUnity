using System.Linq;
using GokouKotori.FBXUVTextureTransfer;
using nadena.dev.ndmf;
using UnityEngine;

[assembly: ExportsPlugin(typeof(GokouKotori.FBXUVTextureTransfer.Editor.NDMF.FBXUVTextureTransferNDMFPlugin))]

namespace GokouKotori.FBXUVTextureTransfer.Editor.NDMF
{
    [RunsOnPlatforms(WellKnownPlatforms.VRChatAvatar30)]
    internal sealed class FBXUVTextureTransferNDMFPlugin : Plugin<FBXUVTextureTransferNDMFPlugin>
    {
        public override string QualifiedName => "com.gokoukotori.fbx-uv-texture-transfer";
        public override string DisplayName => "FBX UV Texture Transfer";

        protected override void Configure()
        {
            InPhase(BuildPhase.Resolving)
                .BeforePlugin("net.rs64.tex-trans-tool")
                .Run(FBXUVTextureTransferValidationPass.Instance);

            // ExternalToolAsLayer 側がinterface実装を RequireComponent として扱うため、
            // TTT がwrapperをpurgeした後で本コンポーネントを除去する。
            // 転写画像は Transforming で評価済みなので、Optimizing での除去は出力へ影響しない。
            InPhase(BuildPhase.Optimizing)
                .AfterPlugin("net.rs64.tex-trans-tool")
                .Run(FBXUVTextureTransferCleanupPass.Instance);
        }
    }

    internal sealed class FBXUVTextureTransferValidationPass : Pass<FBXUVTextureTransferValidationPass>
    {
        public override string DisplayName => "FBX UV Texture Transfer の設定を検証";

        protected override void Execute(BuildContext context)
        {
            var layers = context.AvatarRootObject
                .GetComponentsInChildren<FBXUVTextureTransferLayer>(true)
                .Where(layer => layer.isActiveAndEnabled)
                .ToArray();
            if (layers.Length == 0)
            {
                return;
            }

            // 環境エラーをLayerごとに重複表示しない。
            var environmentIssues = new System.Collections.Generic.List<FBXUVTransferValidationIssue>();
            FBXUVTextureTransferValidation.ValidateEnvironment(environmentIssues, layers[0]);
            Report(environmentIssues);

            foreach (var layer in layers)
            {
                Report(FBXUVTextureTransferValidation.ValidateLayer(layer, false));
            }
        }

        private static void Report(System.Collections.Generic.IEnumerable<FBXUVTransferValidationIssue> issues)
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

    internal sealed class FBXUVTextureTransferCleanupPass : Pass<FBXUVTextureTransferCleanupPass>
    {
        public override string DisplayName => "FBX UV Texture Transfer のビルド用コンポーネントを除去";

        protected override void Execute(BuildContext context)
        {
            var layers = context.AvatarRootObject.GetComponentsInChildren<FBXUVTextureTransferLayer>(true);
            foreach (var layer in layers)
            {
                Object.DestroyImmediate(layer);
            }
        }
    }
}
