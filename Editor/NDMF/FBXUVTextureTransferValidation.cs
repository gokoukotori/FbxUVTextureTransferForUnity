using System;
using System.Collections.Generic;
using System.Linq;
using GokouKotori.FBXUVTextureTransfer;
using net.rs64.TexTransTool.MultiLayerImage;
using UnityEditor;
using Unity.Profiling;
using UnityEngine;
using Object = UnityEngine.Object;

namespace GokouKotori.FBXUVTextureTransfer.Editor.NDMF
{
    internal readonly struct FBXUVTransferValidationIssue
    {
        internal FBXUVTransferValidationIssue(string message, Object contextObject)
        {
            Message = message;
            ContextObject = contextObject;
        }

        internal string Message { get; }
        internal Object ContextObject { get; }
    }

    internal static class FBXUVTextureTransferValidation
    {
        private static readonly ProfilerMarker ValidationMarker =
            new ProfilerMarker("FBXUVTextureTransfer.Validation");

        internal static IReadOnlyList<FBXUVTransferValidationIssue> ValidateLayer(
            FBXUVTextureTransferLayer layer,
            bool validateEnvironment = true)
        {
            return ValidateLayer(layer, validateEnvironment, new FBXUVMeshAnalysisCache());
        }

        internal static IReadOnlyList<FBXUVTransferValidationIssue> ValidateLayer(
            FBXUVTextureTransferLayer layer,
            bool validateEnvironment,
            FBXUVMeshAnalysisCache analysisCache)
        {
            if (analysisCache == null) throw new ArgumentNullException(nameof(analysisCache));
            using (ValidationMarker.Auto())
            {
                return ValidateLayerCore(layer, validateEnvironment, analysisCache);
            }
        }

        private static IReadOnlyList<FBXUVTransferValidationIssue> ValidateLayerCore(
            FBXUVTextureTransferLayer layer,
            bool validateEnvironment,
            FBXUVMeshAnalysisCache analysisCache)
        {
            var issues = new List<FBXUVTransferValidationIssue>();
            if (layer == null)
            {
                issues.Add(new FBXUVTransferValidationIssue("Layer が見つかりません。", null));
                return issues;
            }

            if (validateEnvironment)
            {
                ValidateEnvironment(issues, layer);
            }

            GameObject avatarRoot = null;
            if (!FBXUVAvatarRootResolver.TryResolve(layer, out avatarRoot, out var avatarRootError))
            {
                Add(issues, avatarRootError, layer);
            }

            ValidateExternalTool(layer, issues);
            var canvas = ValidateHierarchy(layer, issues);
            Texture targetTexture = null;
            if (canvas != null && (canvas.TargetTexture == null || canvas.TargetTexture.SelectTexture == null))
            {
                Add(issues, "親 MultiLayerImageCanvas の TargetTexture が未設定です。", canvas);
            }
            else if (canvas != null)
            {
                targetTexture = canvas.TargetTexture.SelectTexture;
            }

            var serializedLayer = new SerializedObject(layer);
            var sourceModelOrPrefab = ObjectReference<GameObject>(serializedLayer, "sourceModelOrPrefab");
            var targetModelOrPrefab = ObjectReference<GameObject>(serializedLayer, "targetModelOrPrefab");
            var isSourceRootValid = ValidateModelOrPrefabRoot(
                sourceModelOrPrefab,
                "Source",
                layer,
                issues);
            var isTargetRootValid = ValidateModelOrPrefabRoot(
                targetModelOrPrefab,
                "Target",
                layer,
                issues);
            var targetCandidates = isTargetRootValid && targetTexture != null
                ? FBXUVTargetMeshCollector.Collect(targetModelOrPrefab, targetTexture)
                : null;
            var defaultTexture = ObjectReference<Texture2D>(serializedLayer, "defaultSourceTexture");
            var bindings = serializedLayer.FindProperty("regionBindings");

            if (defaultTexture == null)
            {
                Add(issues, "source Texture が未設定です。", layer);
            }

            if (bindings == null || !bindings.isArray || bindings.arraySize == 0)
            {
                Add(issues, "Region Binding が空です。少なくとも1つ追加してください。空リストを全regionとして扱うことはできません。", layer);
                ValidateResources(layer, issues);
                return issues;
            }

            var boundNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var enabledCount = 0;

            for (var index = 0; index < bindings.arraySize; index++)
            {
                var binding = bindings.GetArrayElementAtIndex(index);
                var bindingValue = layer.regionBindings != null && index < layer.regionBindings.Count
                    ? layer.regionBindings[index]
                    : null;
                if (bindingValue == null)
                {
                    Add(issues, $"Region Binding [{index}] が未設定です。", layer);
                    continue;
                }

                var enabled = binding.FindPropertyRelative("enabled");
                if (enabled != null && !enabled.boolValue)
                {
                    continue;
                }

                enabledCount++;
                var rawName = binding.FindPropertyRelative("name")?.stringValue;
                var name = rawName?.Trim();
                if (string.IsNullOrEmpty(name))
                {
                    Add(issues, $"Region Binding [{index}] の名前が空です。", layer);
                    continue;
                }

                if (!boundNames.Add(name))
                {
                    Add(issues, $"Region Binding '{name}' が重複しています。名前は前後の空白を除去して大文字小文字を区別せず照合されます。", layer);
                    continue;
                }

                var orientationProperty = binding.FindPropertyRelative("orientation");
                var orientation = orientationProperty == null
                    ? FBXUVTransferOrientation.Preserve
                    : (FBXUVTransferOrientation)orientationProperty.intValue;
                if (!FBXUVDeformationUtility.IsDefinedOrientation(orientation))
                {
                    Add(issues, $"Region Binding '{name}' の向き補正値が不正です。向き補正を選び直してください。", layer);
                }

                var sourceRegion = binding.FindPropertyRelative("sourceRegion");
                if (bindingValue.sourceRegion == null || sourceRegion == null)
                {
                    Add(issues, $"Source region '{name}' が未設定です。", layer);
                }
                else
                {
                    ValidateRegion(sourceRegion, $"Source region '{name}'", layer, issues, analysisCache);
                    if (isSourceRootValid)
                    {
                        ValidateRegionMeshInRoot(
                            sourceRegion,
                            $"Source region '{name}'",
                            "Source",
                            sourceModelOrPrefab,
                            layer,
                            issues);
                    }
                }

                var targetRegion = binding.FindPropertyRelative("targetRegion");
                if (bindingValue.targetRegion == null || targetRegion == null)
                {
                    Add(issues, $"Target region '{name}' が未設定です。", layer);
                }
                else
                {
                    ValidateRegion(targetRegion, $"Target region '{name}'", layer, issues, analysisCache);
                    if (isTargetRootValid)
                    {
                        ValidateTargetRegionInRoot(
                            targetRegion,
                            name,
                            targetModelOrPrefab,
                            targetCandidates,
                            layer,
                            issues);
                    }
                    ValidateTargetMeshInAvatar(targetRegion, name, layer, avatarRoot, issues);
                }

            }

            if (enabledCount == 0)
            {
                Add(issues, "有効な Region Binding がありません。", layer);
            }

            ValidateResources(layer, issues);
            return issues;
        }

        private static bool ValidateModelOrPrefabRoot(
            GameObject root,
            string label,
            Object contextObject,
            ICollection<FBXUVTransferValidationIssue> issues)
        {
            if (root == null)
            {
                Add(issues, $"{label} Model/Prefab が未設定です。コンポーネントでProject内のModelまたはPrefabのメインルートを指定してください。", contextObject);
                return false;
            }

            if (FBXUVModelPrefabReferenceUtility.TryValidateRoot(root, out var error)) return true;

            Add(issues, $"{label} Model/Prefab が無効です: {error}", root);
            return false;
        }

        internal static void ValidateEnvironment(List<FBXUVTransferValidationIssue> issues, Object contextObject)
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

        private static void ValidateExternalTool(
            FBXUVTextureTransferLayer layer,
            ICollection<FBXUVTransferValidationIssue> issues)
        {
            var wrappers = layer.GetComponents<ExternalToolAsLayer>();
            if (wrappers.Length != 1)
            {
                Add(issues, "同じ GameObject に ExternalToolAsLayer がちょうど1つ必要です。", layer);
            }

            var implementations = layer.GetComponents<MonoBehaviour>()
                .Where(component => component is IExternalToolCanBehaveAsLayer)
                .ToArray();
            if (implementations.Length != 1 || !ReferenceEquals(implementations[0], layer))
            {
                Add(issues, "同じ GameObject の ExternalTool実装は FBXUVTextureTransferLayer 1つだけにしてください。", layer);
            }
        }

        private static MultiLayerImageCanvas ValidateHierarchy(
            FBXUVTextureTransferLayer layer,
            ICollection<FBXUVTransferValidationIssue> issues)
        {
            var current = layer.transform.parent;
            if (current == null)
            {
                Add(issues, "Layerを MultiLayerImageCanvas または LayerFolder の子に配置してください。", layer);
                return null;
            }

            while (current != null)
            {
                var canvas = current.GetComponent<MultiLayerImageCanvas>();
                if (canvas != null)
                {
                    return canvas;
                }

                if (current.GetComponent<LayerFolder>() == null)
                {
                    Add(issues, "Layerから MultiLayerImageCanvas までの全ての中間親には LayerFolder が必要です。", current.gameObject);
                    return null;
                }

                current = current.parent;
            }

            Add(issues, "親階層に MultiLayerImageCanvas がありません。", layer);
            return null;
        }

        private static void ValidateRegion(
            SerializedProperty region,
            string label,
            Object contextObject,
            ICollection<FBXUVTransferValidationIssue> issues,
            FBXUVMeshAnalysisCache analysisCache)
        {
            var mesh = region.FindPropertyRelative("mesh")?.objectReferenceValue as Mesh;
            if (mesh == null)
            {
                Add(issues, $"{label} の Mesh が未設定です。EditorWindowでislandを再選択してください。", contextObject);
                return;
            }

            var subMeshIndex = region.FindPropertyRelative("subMeshIndex")?.intValue ?? -1;
            if (subMeshIndex < 0 || subMeshIndex >= mesh.subMeshCount)
            {
                Add(issues, $"{label} の submesh {subMeshIndex} は Mesh '{mesh.name}' に存在しません。", contextObject);
                return;
            }

            if (mesh.GetTopology(subMeshIndex) != MeshTopology.Triangles)
            {
                Add(issues, $"{label} の submeshはTriangle topologyではありません。", contextObject);
                return;
            }

            var uvChannel = region.FindPropertyRelative("uvChannel")?.intValue ?? -1;
            if (uvChannel < 0 || uvChannel > 7)
            {
                Add(issues, $"{label} の UV channel {uvChannel} は範囲外です。", contextObject);
                return;
            }

            try
            {
                var analysis = analysisCache.Get(mesh, subMeshIndex, uvChannel);
                var triangles = analysis.GetTriangles();
                if (triangles == null || triangles.Count == 0)
                {
                    Add(issues, $"{label} のUV triangleを取得できません。UV channelを確認してください。", contextObject);
                }

                var savedHash = region.FindPropertyRelative("meshHash")?.stringValue;
                var currentHash = analysis.GetContentHash();
                if (string.IsNullOrEmpty(savedHash) || !string.Equals(savedHash, currentHash, StringComparison.Ordinal))
                {
                    Add(issues, $"{label} のMesh内容hashが一致しません。自動修復は行わないためEditorWindowでislandを再選択してください。", contextObject);
                }
            }
            catch (Exception exception)
            {
                Add(issues, $"{label} を検証できません: {exception.Message}", contextObject);
            }

            var savedTriangles = region.FindPropertyRelative("triangles");
            if (savedTriangles == null || !savedTriangles.isArray || savedTriangles.arraySize == 0)
            {
                Add(issues, $"{label} に保存されたUV triangleがありません。", contextObject);
            }

            var bounds = region.FindPropertyRelative("bounds");
            var minU = bounds?.FindPropertyRelative("minU")?.floatValue ?? 0f;
            var minV = bounds?.FindPropertyRelative("minV")?.floatValue ?? 0f;
            var maxU = bounds?.FindPropertyRelative("maxU")?.floatValue ?? 0f;
            var maxV = bounds?.FindPropertyRelative("maxV")?.floatValue ?? 0f;
            if (!(maxU > minU) || !(maxV > minV))
            {
                Add(issues, $"{label} のUV boundsが無効です。EditorWindowでislandを再選択してください。", contextObject);
            }
        }

        private static void ValidateTargetMeshInAvatar(
            SerializedProperty region,
            string regionName,
            Object contextObject,
            GameObject avatarRoot,
            ICollection<FBXUVTransferValidationIssue> issues)
        {
            var mesh = region.FindPropertyRelative("mesh")?.objectReferenceValue as Mesh;
            if (mesh == null || avatarRoot == null) return;

            var found = avatarRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true)
                .Any(renderer => renderer.sharedMesh == mesh)
                || avatarRoot.GetComponentsInChildren<MeshFilter>(true)
                    .Any(filter => filter.sharedMesh == mesh);
            if (!found)
            {
                Add(
                    issues,
                    $"Target region '{regionName}' の Mesh '{mesh.name}' は VRChat Avatar Root 配下のRendererで使用されていません。Target regionを確認してください。",
                    contextObject);
            }
        }

        private static void ValidateRegionMeshInRoot(
            SerializedProperty region,
            string regionLabel,
            string rootLabel,
            GameObject root,
            Object contextObject,
            ICollection<FBXUVTransferValidationIssue> issues)
        {
            var mesh = region.FindPropertyRelative("mesh")?.objectReferenceValue as Mesh;
            if (mesh == null || FBXUVModelPrefabReferenceUtility.ContainsMesh(root, mesh)) return;

            Add(
                issues,
                $"{regionLabel} の Mesh '{mesh.name}' は {rootLabel} Model/Prefab 配下のRendererまたはMeshFilterで使用されていません。{regionLabel}を選び直してください。",
                contextObject);
        }

        private static void ValidateTargetRegionInRoot(
            SerializedProperty region,
            string regionName,
            GameObject targetRoot,
            IReadOnlyList<FBXUVTargetMeshCandidate> targetCandidates,
            Object contextObject,
            ICollection<FBXUVTransferValidationIssue> issues)
        {
            var mesh = region.FindPropertyRelative("mesh")?.objectReferenceValue as Mesh;
            if (mesh == null) return;

            if (!FBXUVModelPrefabReferenceUtility.ContainsMesh(targetRoot, mesh))
            {
                Add(
                    issues,
                    $"Target region '{regionName}' の Mesh '{mesh.name}' は Target Model/Prefab 配下のRendererまたはMeshFilterで使用されていません。Target regionを選び直してください。",
                    contextObject);
                return;
            }

            if (targetCandidates == null) return;

            var subMeshIndex = region.FindPropertyRelative("subMeshIndex")?.intValue ?? -1;
            if (subMeshIndex < 0 || subMeshIndex >= mesh.subMeshCount) return;

            var isCandidate = targetCandidates.Any(candidate =>
                candidate.Mesh == mesh && candidate.SubMeshIndices.Contains(subMeshIndex));
            if (!isCandidate)
            {
                Add(
                    issues,
                    $"Target region '{regionName}' の Mesh '{mesh.name}' / submesh {subMeshIndex} は、親 MultiLayerImageCanvas の TargetTexture を使用する Target Model/Prefab 配下の候補に含まれていません。Target regionを選び直してください。",
                    contextObject);
            }
        }

        private static void ValidateResources(
            FBXUVTextureTransferLayer layer,
            ICollection<FBXUVTransferValidationIssue> issues)
        {
            var packagedTriangleShader = FBXUVTextureTransferResources.LoadTriangleShader();
            var triangleShader = layer.ResolveTriangleShader();
            if (triangleShader == null || packagedTriangleShader == null)
            {
                Add(issues, "変形triangle描画用Shaderを読み込めません。パッケージを再インポートしてください。", layer);
            }
            else if (triangleShader != packagedTriangleShader)
            {
                Add(issues, "変形triangle描画用Shaderがパッケージ既定assetではありません。既定値へ戻してください。", layer);
            }

            var packagedPixelProcessShader = FBXUVTextureTransferResources.LoadPixelProcessShader();
            var pixelProcessShader = layer.ResolvePixelProcessShader();
            if (pixelProcessShader == null || packagedPixelProcessShader == null)
            {
                Add(issues, "穴埋め・bleed用ComputeShaderを読み込めません。パッケージを再インポートしてください。", layer);
            }
            else if (pixelProcessShader != packagedPixelProcessShader)
            {
                Add(issues, "穴埋め・bleed用ComputeShaderがパッケージ既定assetではありません。既定値へ戻してください。", layer);
            }
            else
            {
                ValidateComputeKernel(pixelProcessShader, "InitializeSeeds", layer, issues);
                ValidateComputeKernel(pixelProcessShader, "PropagateSeeds", layer, issues);
                ValidateComputeKernel(pixelProcessShader, "ResolveFill", layer, issues);
                ValidateComputeKernel(pixelProcessShader, "Dilate8Connected", layer, issues);
                ValidateComputeKernel(pixelProcessShader, "CompositeStraightAlpha", layer, issues);
            }
        }

        private static void ValidateComputeKernel(
            ComputeShader shader,
            string kernelName,
            Object contextObject,
            ICollection<FBXUVTransferValidationIssue> issues)
        {
            if (!shader.HasKernel(kernelName))
            {
                Add(issues, $"穴埋め・bleed用ComputeShaderにkernel '{kernelName}' がありません。", contextObject);
            }
        }

        private static T ObjectReference<T>(SerializedObject serializedObject, string propertyName)
            where T : Object
        {
            return serializedObject.FindProperty(propertyName)?.objectReferenceValue as T;
        }

        private static void Add(
            ICollection<FBXUVTransferValidationIssue> issues,
            string message,
            Object contextObject)
        {
            issues.Add(new FBXUVTransferValidationIssue(message, contextObject));
        }
    }
}
