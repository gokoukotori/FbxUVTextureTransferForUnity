using System;
using System.Collections.Generic;
using UnityEngine;

namespace GokouKotori.FBXUVTextureTransfer
{
    [AddComponentMenu("Gokoukotori/FBX UV Texture Transfer/FBX UV Texture Transfer Layer")]
    [DisallowMultipleComponent]
    public sealed class FBXUVTextureTransferLayer : FBXUVTransferLayerBase
    {
        public Texture2D defaultSourceTexture;
        public List<FBXUVRegionBinding> regionBindings = new List<FBXUVRegionBinding>();
        public Shader triangleShader;
        public ComputeShader pixelProcessShader;

        public Texture2D DefaultSourceTexture { get { return defaultSourceTexture; } set { defaultSourceTexture = value; } }
        public List<FBXUVRegionBinding> RegionBindings { get { return regionBindings; } }

        private protected override string LoadImageFailureMessage =>
            "FBX UV Texture Transferのレイヤープレビュー生成に失敗しました。透明レイヤーを返します。";
        private protected override string LoadImageFallbackFailureMessage =>
            "FBX UV Texture Transferの透明fallback生成にも失敗しました。";

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
            if (regionBindings == null || regionBindings.Count == 0) return Fail("Regionがありません。", out reason);
            if (defaultSourceTexture == null) return Fail("転送元テクスチャが未設定です。", out reason);

            var enabledCount = 0;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var index = 0; index < regionBindings.Count; index++)
            {
                var binding = regionBindings[index];
                if (binding == null) return Fail("Regionが未設定です。", out reason);
                if (!binding.enabled) continue;
                enabledCount++;
                if (string.IsNullOrWhiteSpace(binding.name)) return Fail("有効なRegionの名前が空です。", out reason);
                var normalized = binding.name.Trim();
                if (!FBXUVDeformationUtility.IsDefinedOrientation(binding.orientation))
                {
                    return Fail($"Region「{normalized}」の向き補正が不正です。", out reason);
                }
                if (!seen.Add(normalized)) return Fail("有効なRegionの名前が重複しています。", out reason);
                if (!IsRegionReady(binding.sourceRegion, analysisCache)
                    || !IsRegionReady(binding.targetRegion, analysisCache))
                {
                    return Fail($"Region「{normalized}」のUV島の選択が無効か、メッシュの内容が保存時と異なります。", out reason);
                }
            }
            if (enabledCount == 0) return Fail("有効なRegionがありません。", out reason);
            reason = string.Empty;
            return true;
        }

        private protected override void RenderPrepared(RenderTexture destination)
        {
            if (!CanRender(out _)) return;
            FBXUVTextureTransferRenderer.Render(this, destination);
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

        private protected override void NormalizeSerializedFields()
        {
            if (regionBindings == null) regionBindings = new List<FBXUVRegionBinding>();
        }

        private protected override void AssignMissingResources() => AssignDefaultResources();
    }
}
