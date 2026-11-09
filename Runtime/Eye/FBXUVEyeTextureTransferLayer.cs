using System;
using System.Collections.Generic;
using UnityEngine;

namespace GokouKotori.FBXUVTextureTransfer
{
    [AddComponentMenu("Gokoukotori/FBX UV Texture Transfer/FBX UV Eye Texture Transfer Layer (β版)")]
    [DisallowMultipleComponent]
    public sealed class FBXUVEyeTextureTransferLayer : FBXUVTransferLayerBase
    {
        public Texture2D sourceTexture;
        public List<FBXUVEyeBinding> eyes = new List<FBXUVEyeBinding>();
        public Shader eyeShader;
        public ComputeShader pixelProcessShader;

        private protected override string LoadImageFailureMessage => "FBX UV Eye Texture Transferの生成に失敗しました。透明レイヤーを返します。";
        private protected override string LoadImageFallbackFailureMessage => "FBX UV Eye Texture Transferの透明fallback生成に失敗しました。";
        public Shader ResolveEyeShader() => eyeShader != null ? eyeShader : Resources.Load<Shader>("FBXUVTextureTransfer/EyeTransfer");
        public ComputeShader ResolvePixelProcessShader() => pixelProcessShader != null ? pixelProcessShader : FBXUVTextureTransferResources.LoadPixelProcessShader();
        public bool CanRender(out string reason) => TryPrepare(out _, out reason);

        internal bool TryPrepare(out List<FBXUVEyeMapping> mappings, out string reason)
        {
            mappings = new List<FBXUVEyeMapping>();
            try
            {
                if (!SystemInfo.supportsComputeShaders || ResolveEyeShader() == null || !ResolveEyeShader().isSupported || ResolvePixelProcessShader() == null)
                    throw new ArgumentException("アイ転送用Shader / ComputeShaderを利用できません。");
                if (sourceTexture == null) throw new ArgumentException("転送元テクスチャが未設定です。");
                if (eyes == null || eyes.Count == 0) throw new ArgumentException("目の組み合わせを追加してください。");
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var cache = new FBXUVMeshAnalysisCache();
                foreach (var eye in eyes)
                {
                    if (eye == null) throw new ArgumentException("目の組み合わせが無効です。");
                    if (!eye.enabled) continue;
                    if (string.IsNullOrWhiteSpace(eye.name) || !names.Add(eye.name.Trim()))
                        throw new ArgumentException("有効な目の名前には重複しない名前を付けてください。");
                    try { mappings.Add(FBXUVEyeMapping.Create(sourceModelOrPrefab, targetModelOrPrefab, eye, cache)); }
                    catch (ArgumentException e) { throw new ArgumentException(eye.name + ": " + e.Message, e); }
                }
                if (mappings.Count == 0) throw new ArgumentException("有効な目の組み合わせがありません。");
                reason = string.Empty; return true;
            }
            catch (Exception e) { reason = e.Message; mappings.Clear(); return false; }
        }

        private protected override void RenderPrepared(RenderTexture destination)
        {
            if (!TryPrepare(out var mappings, out _)) return;
            FBXUVEyeTextureTransferRenderer.Render(this, destination, mappings);
        }

        private protected override void NormalizeSerializedFields() => eyes ??= new List<FBXUVEyeBinding>();
        private protected override void AssignMissingResources()
        {
            if (eyeShader == null) eyeShader = ResolveEyeShader();
            if (pixelProcessShader == null) pixelProcessShader = ResolvePixelProcessShader();
        }
    }
}
