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
            return ValidateLayer(layer, validateEnvironment, analysisCache, null);
        }

        internal static IReadOnlyList<FBXUVTransferValidationIssue> ValidateLayerForBuild(
            FBXUVTextureTransferLayer layer,
            GameObject buildAvatarRoot)
        {
            if (buildAvatarRoot == null) throw new ArgumentNullException(nameof(buildAvatarRoot));
            return ValidateLayer(layer, false, new FBXUVMeshAnalysisCache(), buildAvatarRoot);
        }

        private static IReadOnlyList<FBXUVTransferValidationIssue> ValidateLayer(
            FBXUVTextureTransferLayer layer,
            bool validateEnvironment,
            FBXUVMeshAnalysisCache analysisCache,
            GameObject buildAvatarRoot)
        {
            if (analysisCache == null) throw new ArgumentNullException(nameof(analysisCache));
            using (ValidationMarker.Auto())
            {
                return ValidateLayerCore(layer, validateEnvironment, analysisCache, buildAvatarRoot);
            }
        }

        private static IReadOnlyList<FBXUVTransferValidationIssue> ValidateLayerCore(
            FBXUVTextureTransferLayer layer,
            bool validateEnvironment,
            FBXUVMeshAnalysisCache analysisCache,
            GameObject buildAvatarRoot)
        {
            if (!FBXUVLayerValidation.TryBegin(
                    layer,
                    "FBX UV Texture Transfer Layer がありません。",
                    "FBX UV Texture Transfer Layer",
                    validateEnvironment,
                    buildAvatarRoot,
                    useResolvedRootsForBuild: false,
                    out var checks))
            {
                return checks.Issues;
            }

            var issues = checks.Issues;
            var avatarRoot = checks.AvatarRoot;
            var targetTexture = checks.TargetTexture;
            var isSourceRootValid = checks.SourceValid;
            var sourceModelOrPrefab = checks.SourceRoot;
            var isTargetRootValid = checks.TargetValid;
            var targetModelOrPrefab = checks.TargetRoot;

            var serializedLayer = new SerializedObject(layer);
            var targetCandidates = isTargetRootValid && targetTexture != null
                ? FBXUVTargetMeshCollector.Collect(targetModelOrPrefab, targetTexture)
                : null;
            var avatarCandidates = avatarRoot != null && targetTexture != null
                ? FBXUVTargetMeshCollector.Collect(avatarRoot, targetTexture)
                : null;
            var defaultTexture = ObjectReference<Texture2D>(serializedLayer, "defaultSourceTexture");
            var bindings = serializedLayer.FindProperty("regionBindings");

            if (defaultTexture == null)
            {
                Add(issues, "転送元テクスチャが未設定です。Inspector の「転送元テクスチャ」を設定してください。", layer);
            }

            if (bindings == null || !bindings.isArray || bindings.arraySize == 0)
            {
                Add(issues, $"Region の一覧が空です。{FBXUVEditorNames.TextureTransferEditor}で少なくとも1つ追加してください。空の一覧を全Regionとして扱うことはできません。", layer);
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
                    Add(issues, $"{index + 1}番目の Region が未設定です。", layer);
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
                    Add(issues, $"{index + 1}番目の Region の名前が空です。", layer);
                    continue;
                }

                if (!boundNames.Add(name))
                {
                    Add(issues, $"Region「{name}」が重複しています。名前は前後の空白を除去して大文字小文字を区別せず照合されます。", layer);
                    continue;
                }

                var orientationProperty = binding.FindPropertyRelative("orientation");
                var orientation = orientationProperty == null
                    ? FBXUVTransferOrientation.Preserve
                    : (FBXUVTransferOrientation)orientationProperty.intValue;
                if (!FBXUVDeformationUtility.IsDefinedOrientation(orientation))
                {
                    Add(issues, $"Region「{name}」の向き補正値が不正です。向き補正を選び直してください。", layer);
                }

                var sourceRegion = binding.FindPropertyRelative("sourceRegion");
                var sourceLabel = $"Region「{name}」の転送元";
                if (bindingValue.sourceRegion == null || sourceRegion == null)
                {
                    Add(issues, $"{sourceLabel}が未設定です。{FBXUVEditorNames.TextureTransferEditor}で設定してください。", layer);
                }
                else
                {
                    ValidateRegion(sourceRegion, sourceLabel, layer, issues, analysisCache);
                    if (isSourceRootValid)
                    {
                        ValidateRegionMeshInRoot(
                            sourceRegion,
                            sourceLabel,
                            "転送元",
                            sourceModelOrPrefab,
                            layer,
                            issues);
                    }
                }

                var targetRegion = binding.FindPropertyRelative("targetRegion");
                var targetLabel = $"Region「{name}」の転送先";
                if (bindingValue.targetRegion == null || targetRegion == null)
                {
                    Add(issues, $"{targetLabel}が未設定です。{FBXUVEditorNames.TextureTransferEditor}で設定してください。", layer);
                }
                else
                {
                    ValidateRegion(targetRegion, targetLabel, layer, issues, analysisCache);
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
                    ValidateTargetMeshInAvatar(targetRegion, name, layer, avatarRoot, avatarCandidates, issues);
                }

            }

            if (enabledCount == 0)
            {
                Add(issues, "有効な Region がありません。", layer);
            }

            ValidateResources(layer, issues);
            return issues;
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
                Add(issues, $"{label}のメッシュが未設定です。{FBXUVEditorNames.TextureTransferEditor}でUV島を選び直してください。", contextObject);
                return;
            }

            var subMeshIndex = region.FindPropertyRelative("subMeshIndex")?.intValue ?? -1;
            if (subMeshIndex < 0 || subMeshIndex >= mesh.subMeshCount)
            {
                Add(issues, $"{label}のサブメッシュ {subMeshIndex} はメッシュ「{mesh.name}」に存在しません。", contextObject);
                return;
            }

            if (mesh.GetTopology(subMeshIndex) != MeshTopology.Triangles)
            {
                Add(issues, $"{label}のサブメッシュは三角形のメッシュではありません。", contextObject);
                return;
            }

            var uvChannel = region.FindPropertyRelative("uvChannel")?.intValue ?? -1;
            if (uvChannel < 0 || uvChannel > 7)
            {
                Add(issues, $"{label}のUVチャンネル {uvChannel} は範囲外です。", contextObject);
                return;
            }

            try
            {
                var analysis = analysisCache.Get(mesh, subMeshIndex, uvChannel);
                var triangles = analysis.GetTriangles();
                if (triangles == null || triangles.Count == 0)
                {
                    Add(issues, $"{label}のUVの三角形を取得できません。UVチャンネルを確認してください。", contextObject);
                }

                var savedHash = region.FindPropertyRelative("meshHash")?.stringValue;
                var currentHash = analysis.GetContentHash();
                if (string.IsNullOrEmpty(savedHash) || !string.Equals(savedHash, currentHash, StringComparison.Ordinal))
                {
                    Add(issues, $"{label}のメッシュの内容が保存時と異なります。自動修復は行わないため{FBXUVEditorNames.TextureTransferEditor}でUV島を選び直してください。", contextObject);
                }
            }
            catch (Exception exception)
            {
                Add(issues, $"{label}を検証できません: {exception.Message}", contextObject);
            }

            var savedTriangles = region.FindPropertyRelative("triangles");
            if (savedTriangles == null || !savedTriangles.isArray || savedTriangles.arraySize == 0)
            {
                Add(issues, $"{label}に保存されたUVの三角形がありません。", contextObject);
            }

            var bounds = region.FindPropertyRelative("bounds");
            var minU = bounds?.FindPropertyRelative("minU")?.floatValue ?? 0f;
            var minV = bounds?.FindPropertyRelative("minV")?.floatValue ?? 0f;
            var maxU = bounds?.FindPropertyRelative("maxU")?.floatValue ?? 0f;
            var maxV = bounds?.FindPropertyRelative("maxV")?.floatValue ?? 0f;
            if (!(maxU > minU) || !(maxV > minV))
            {
                Add(issues, $"{label}のUVの範囲が無効です。{FBXUVEditorNames.TextureTransferEditor}でUV島を選び直してください。", contextObject);
            }
        }

        // avatarCandidates: the avatar's meshes / submeshes drawn with the canvas TargetTexture, or null without one.
        private static void ValidateTargetMeshInAvatar(
            SerializedProperty region,
            string regionName,
            Object contextObject,
            GameObject avatarRoot,
            IReadOnlyList<FBXUVTargetMeshCandidate> avatarCandidates,
            ICollection<FBXUVTransferValidationIssue> issues)
        {
            var mesh = region.FindPropertyRelative("mesh")?.objectReferenceValue as Mesh;
            if (mesh == null || avatarRoot == null) return;

            var found = FBXUVModelPrefabReferenceUtility.ContainsMesh(avatarRoot, mesh);
            if (!found)
            {
                Add(
                    issues,
                    $"Region「{regionName}」の転送先のメッシュ「{mesh.name}」はアバター配下のRendererで使用されていません。Region「{regionName}」の転送先を確認してください。",
                    contextObject);
                return;
            }

            if (avatarCandidates == null) return;

            // An out-of-range submesh is already reported by ValidateRegion.
            var subMeshIndex = region.FindPropertyRelative("subMeshIndex")?.intValue ?? -1;
            if (subMeshIndex < 0 || subMeshIndex >= mesh.subMeshCount) return;

            if (!avatarCandidates.Any(candidate => candidate.Mesh == mesh && candidate.SubMeshIndices.Contains(subMeshIndex)))
            {
                Add(
                    issues,
                    $"Region「{regionName}」の転送先のメッシュ「{mesh.name}」/ サブメッシュ {subMeshIndex} を、Canvasの対象テクスチャを使って描画するRendererがアバター配下にありません。Region「{regionName}」の転送先またはアバターのMaterialを確認してください。",
                    contextObject);
            }
        }

        // Adds an issue when the region's mesh is set but not used under root. Returns true only when it is used.
        private static bool ValidateRegionMeshInRoot(
            SerializedProperty region,
            string regionLabel,
            string rootLabel,
            GameObject root,
            Object contextObject,
            ICollection<FBXUVTransferValidationIssue> issues)
        {
            var mesh = region.FindPropertyRelative("mesh")?.objectReferenceValue as Mesh;
            if (mesh == null) return false;
            if (FBXUVModelPrefabReferenceUtility.ContainsMesh(root, mesh)) return true;

            Add(
                issues,
                $"{regionLabel}のメッシュ「{mesh.name}」は{rootLabel} Model / Prefab 配下のRendererまたはMeshFilterで使用されていません。{FBXUVEditorNames.TextureTransferEditor}で{regionLabel}を選び直してください。",
                contextObject);
            return false;
        }

        private static void ValidateTargetRegionInRoot(
            SerializedProperty region,
            string regionName,
            GameObject targetRoot,
            IReadOnlyList<FBXUVTargetMeshCandidate> targetCandidates,
            Object contextObject,
            ICollection<FBXUVTransferValidationIssue> issues)
        {
            if (!ValidateRegionMeshInRoot(
                    region, $"Region「{regionName}」の転送先", "転送先", targetRoot, contextObject, issues))
                return;

            if (targetCandidates == null) return;

            var mesh = region.FindPropertyRelative("mesh")?.objectReferenceValue as Mesh;
            var subMeshIndex = region.FindPropertyRelative("subMeshIndex")?.intValue ?? -1;
            if (subMeshIndex < 0 || subMeshIndex >= mesh.subMeshCount) return;

            var isCandidate = targetCandidates.Any(candidate =>
                candidate.Mesh == mesh && candidate.SubMeshIndices.Contains(subMeshIndex));
            if (!isCandidate)
            {
                Add(
                    issues,
                    $"Region「{regionName}」の転送先のメッシュ「{mesh.name}」/ サブメッシュ {subMeshIndex} は、Canvasの対象テクスチャを使用する転送先 Model / Prefab 配下の候補に含まれていません。{FBXUVEditorNames.TextureTransferEditor}でRegion「{regionName}」の転送先を選び直してください。",
                    contextObject);
            }
        }

        private static void ValidateResources(
            FBXUVTextureTransferLayer layer,
            ICollection<FBXUVTransferValidationIssue> issues)
        {
            ValidatePackagedResource(
                FBXUVTextureTransferResources.LoadTriangleShader(), layer.ResolveTriangleShader(),
                "三角形変形描画用Shader", layer, issues);

            var packagedPixelProcessShader = FBXUVTextureTransferResources.LoadPixelProcessShader();
            var pixelProcessShader = layer.ResolvePixelProcessShader();
            if (ValidatePackagedResource(packagedPixelProcessShader, pixelProcessShader, "穴埋め・bleed用ComputeShader", layer, issues))
            {
                ValidateComputeKernel(pixelProcessShader, "InitializeSeeds", layer, issues);
                ValidateComputeKernel(pixelProcessShader, "PropagateSeeds", layer, issues);
                ValidateComputeKernel(pixelProcessShader, "ResolveFill", layer, issues);
                ValidateComputeKernel(pixelProcessShader, "Dilate8Connected", layer, issues);
                ValidateComputeKernel(pixelProcessShader, "CompositeStraightAlpha", layer, issues);
            }
        }

        // The layer must use the package's own asset. Returns true when it does.
        private static bool ValidatePackagedResource<T>(
            T packaged,
            T resolved,
            string label,
            Object contextObject,
            ICollection<FBXUVTransferValidationIssue> issues)
            where T : Object
        {
            if (resolved == null || packaged == null)
            {
                Add(issues, $"{label}を読み込めません。パッケージを再インポートしてください。", contextObject);
                return false;
            }

            if (resolved != packaged)
            {
                Add(issues, $"{label}がパッケージ既定assetではありません。既定値へ戻してください。", contextObject);
                return false;
            }

            return true;
        }

        private static void ValidateComputeKernel(
            ComputeShader shader,
            string kernelName,
            Object contextObject,
            ICollection<FBXUVTransferValidationIssue> issues)
        {
            if (!shader.HasKernel(kernelName))
            {
                Add(issues, $"穴埋め・bleed用ComputeShaderにkernel「{kernelName}」がありません。", contextObject);
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
