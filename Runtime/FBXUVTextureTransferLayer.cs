using System;
using System.Collections.Generic;
using nadena.dev.ndmf;
using net.rs64.TexTransTool.MultiLayerImage;
using UnityEngine;

namespace GokouKotori.FBXUVTextureTransfer
{
    [AddComponentMenu("Gokoukotori/FBX UV Texture Transfer/FBX UV Texture Transfer Layer")]
    [DisallowMultipleComponent]
    public sealed class FBXUVTextureTransferLayer : MonoBehaviour, IExternalToolCanBehaveAsImageLayerV1, INDMFEditorOnly
    {
        public GameObject sourceModelOrPrefab;
        public GameObject targetModelOrPrefab;
        public Texture2D defaultSourceTexture;
        public List<FBXUVRegionBinding> regionBindings = new List<FBXUVRegionBinding>();
        public Shader triangleShader;
        public ComputeShader pixelProcessShader;

        public GameObject SourceModelOrPrefab { get { return sourceModelOrPrefab; } set { sourceModelOrPrefab = value; } }
        public GameObject TargetModelOrPrefab { get { return targetModelOrPrefab; } set { targetModelOrPrefab = value; } }
        public Texture2D DefaultSourceTexture { get { return defaultSourceTexture; } set { defaultSourceTexture = value; } }
        public List<FBXUVRegionBinding> RegionBindings { get { return regionBindings; } }

        // Apply on Play can evaluate layers from an earlier Awake before this component's OnEnable runs.
        // Keep explicit component and hierarchy disabling semantics without depending on lifecycle callbacks.
        internal bool IsEnabledInHierarchy => enabled && gameObject.activeInHierarchy;

        public Shader ResolveTriangleShader()
        {
            return triangleShader != null ? triangleShader : FBXUVTextureTransferResources.LoadTriangleShader();
        }

        public ComputeShader ResolvePixelProcessShader()
        {
            return pixelProcessShader != null ? pixelProcessShader : FBXUVTextureTransferResources.LoadPixelProcessShader();
        }

        public void AssignDefaultResources()
        {
            if (triangleShader == null) triangleShader = FBXUVTextureTransferResources.LoadTriangleShader();
            if (pixelProcessShader == null) pixelProcessShader = FBXUVTextureTransferResources.LoadPixelProcessShader();
        }

        public bool CanRender(out string reason)
        {
            return CanRender(out reason, new FBXUVMeshAnalysisCache());
        }

        internal bool CanRender(out string reason, FBXUVMeshAnalysisCache analysisCache)
        {
            if (analysisCache == null) throw new ArgumentNullException(nameof(analysisCache));
            if (!SystemInfo.supportsComputeShaders) return Fail("ComputeShaderがサポートされていません。", out reason);
            if (ResolveTriangleShader() == null || ResolvePixelProcessShader() == null) return Fail("Shaderリソースを読み込めません。", out reason);
            if (regionBindings == null || regionBindings.Count == 0) return Fail("Region bindingがありません。", out reason);
            if (defaultSourceTexture == null) return Fail("source Textureが未設定です。", out reason);

            var enabledCount = 0;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var index = 0; index < regionBindings.Count; index++)
            {
                var binding = regionBindings[index];
                if (binding == null) return Fail("Region bindingが未設定です。", out reason);
                if (!binding.enabled) continue;
                enabledCount++;
                if (string.IsNullOrWhiteSpace(binding.name)) return Fail("有効なRegion bindingの名前が空です。", out reason);
                var normalized = binding.name.Trim();
                if (!FBXUVDeformationUtility.IsDefinedOrientation(binding.orientation))
                {
                    return Fail("Region bindingの向き補正が不正です: " + normalized, out reason);
                }
                if (!seen.Add(normalized)) return Fail("有効なRegion binding名が重複しています。", out reason);
                if (!IsRegionReady(binding.sourceRegion, analysisCache)
                    || !IsRegionReady(binding.targetRegion, analysisCache))
                {
                    return Fail("Regionの選択またはMesh hashが無効です: " + normalized, out reason);
                }
            }
            if (enabledCount == 0) return Fail("有効なRegion bindingがありません。", out reason);
            reason = string.Empty;
            return true;
        }

        public bool HasExclusiveExternalToolImplementation()
        {
            var behaviours = GetComponents<MonoBehaviour>();
            var count = 0;
            for (var index = 0; index < behaviours.Length; index++)
            {
                if (behaviours[index] is IExternalToolCanBehaveAsLayer) count++;
            }
            return count == 1;
        }

        public void LoadImage(RenderTexture writeDistentionTexture)
        {
            if (writeDistentionTexture == null) return;
            try
            {
                FBXUVTextureTransferRenderer.Clear(writeDistentionTexture);
                string reason;
                if (!IsEnabledInHierarchy || !CanRender(out reason)) return;
                FBXUVTextureTransferRenderer.Render(this, writeDistentionTexture);
            }
            catch (Exception exception)
            {
                try
                {
                    FBXUVTextureTransferRenderer.Clear(writeDistentionTexture);
                }
                catch (Exception clearException)
                {
                    Debug.LogError("FBX UV Texture Transferの透明fallback生成にも失敗しました。\n" + clearException, this);
                }
                Debug.LogError("FBX UV Texture Transferのレイヤープレビュー生成に失敗しました。透明レイヤーを返します。\n" + exception, this);
            }
        }

        private static bool IsRegionReady(FBXUVTransferRegion region, FBXUVMeshAnalysisCache analysisCache)
        {
            return region != null
                && region.bounds.IsValid
                && region.triangles != null
                && region.triangles.Count > 0
                && FBXUVMeshUtility.IsMeshHashCurrent(region, analysisCache);
        }

        private static bool Fail(string message, out string reason)
        {
            reason = message;
            return false;
        }

        private void Reset()
        {
            EnsureExternalToolAsLayer();
            AssignDefaultResources();
        }

        private void OnValidate()
        {
            if (regionBindings == null) regionBindings = new List<FBXUVRegionBinding>();
            EnsureExternalToolAsLayer();
            AssignDefaultResources();
        }

        private void EnsureExternalToolAsLayer()
        {
            if (GetComponent<ExternalToolAsLayer>() == null)
            {
                gameObject.AddComponent<ExternalToolAsLayer>();
            }
        }
    }
}
