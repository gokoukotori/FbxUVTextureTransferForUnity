using System.Collections.Generic;
using UnityEngine;
using Object = UnityEngine.Object;

namespace GokouKotori.FBXUVTextureTransfer.Editor.NDMF
{
    // The environment every transfer layer needs: a supported TexTransTool version on the Unity backend, and compute
    // shaders. The build checks it once; an inspector validation checks it with each layer.
    internal static class FBXUVEnvironmentValidation
    {
        internal static void Validate(List<FBXUVTransferValidationIssue> issues, Object contextObject)
        {
            if (!TexTransToolCompatibility.TryGetStatus(out var status, out var failure))
            {
                Add(issues, failure, contextObject);
                return;
            }

            ValidateCompatibilityStatus(issues, status, contextObject);

            if (!SystemInfo.supportsComputeShaders)
            {
                Add(issues, "この環境は ComputeShader をサポートしていません。", contextObject);
            }
        }

        internal static void ValidateCompatibilityStatus(
            List<FBXUVTransferValidationIssue> issues,
            TexTransToolCompatibilityStatus status,
            Object contextObject)
        {
            if (!TexTransToolCompatibility.TryIsVersionAtLeastMinimum(
                    status.Version,
                    out var isAtLeastMinimum,
                    out var versionFailure))
            {
                Add(issues, versionFailure, contextObject);
            }
            else if (!isAtLeastMinimum)
            {
                Add(issues,
                    $"TexTransTool {TexTransToolCompatibility.MinimumVersion} 以上が必要です。現在は {status.Version} です。",
                    contextObject);
            }

            if (!status.IsUnityBackend)
            {
                Add(issues, "TexTransTool の backend が WGPU です。このLayerは Unity backendだけをサポートします。", contextObject);
            }
        }

        private static void Add(List<FBXUVTransferValidationIssue> issues, string message, Object contextObject)
        {
            issues.Add(new FBXUVTransferValidationIssue(message, contextObject));
        }
    }
}
