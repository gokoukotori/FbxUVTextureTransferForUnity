using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf;
using net.rs64.TexTransTool.MultiLayerImage;
using UnityEngine;

namespace GokouKotori.FBXUVTextureTransfer
{
    [AddComponentMenu("Gokoukotori/FBX UV Texture Transfer/FBX UV Eye Texture Transfer Layer")]
    [DisallowMultipleComponent]
    public sealed class FBXUVEyeTextureTransferLayer : MonoBehaviour, IExternalToolCanBehaveAsImageLayerV1, INDMFEditorOnly
    {
        public GameObject sourceModelOrPrefab;
        public GameObject targetModelOrPrefab;
        public Texture2D sourceTexture;
        public List<FBXUVEyeBinding> eyes = new List<FBXUVEyeBinding>();
        public Shader eyeShader;
        public ComputeShader pixelProcessShader;

        internal bool IsEnabledInHierarchy => enabled && gameObject.activeInHierarchy;
        public Shader ResolveEyeShader() => eyeShader != null ? eyeShader : Resources.Load<Shader>("FBXUVTextureTransfer/EyeTransfer");
        public ComputeShader ResolvePixelProcessShader() => pixelProcessShader != null ? pixelProcessShader : FBXUVTextureTransferResources.LoadPixelProcessShader();
        public bool HasExclusiveExternalToolImplementation() => GetComponents<MonoBehaviour>().Count(b => b is IExternalToolCanBehaveAsLayer) == 1;
        public bool CanRender(out string reason) => TryPrepare(out _, out reason);

        internal bool TryPrepare(out List<FBXUVEyeMapping> mappings, out string reason)
        {
            mappings = new List<FBXUVEyeMapping>();
            try
            {
                if (!SystemInfo.supportsComputeShaders || ResolveEyeShader() == null || !ResolveEyeShader().isSupported || ResolvePixelProcessShader() == null)
                    throw new ArgumentException("Eye転送のShader / ComputeShaderを利用できません。");
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

        public void LoadImage(RenderTexture writeDistentionTexture)
        {
            if (writeDistentionTexture == null) return;
            try
            {
                FBXUVTextureTransferRenderer.Clear(writeDistentionTexture);
                if (!IsEnabledInHierarchy || !TryPrepare(out var mappings, out _)) return;
                FBXUVEyeTextureTransferRenderer.Render(this, writeDistentionTexture, mappings);
            }
            catch (Exception e)
            {
                FBXUVTextureTransferRenderer.Clear(writeDistentionTexture);
                Debug.LogError("FBX UV Eye Texture Transferの生成に失敗しました。透明レイヤーを返します。\n" + e, this);
            }
        }

        private void Reset() => EnsureResources();
        private void OnValidate()
        {
            eyes ??= new List<FBXUVEyeBinding>();
            EnsureResources();
        }
        private void EnsureResources()
        {
            if (GetComponent<ExternalToolAsLayer>() == null) gameObject.AddComponent<ExternalToolAsLayer>();
            if (eyeShader == null) eyeShader = ResolveEyeShader();
            if (pixelProcessShader == null) pixelProcessShader = ResolvePixelProcessShader();
        }
    }
}
