using System;
using System.Collections.Generic;
using nadena.dev.ndmf;
using net.rs64.TexTransTool.MultiLayerImage;
using UnityEngine;

namespace GokouKotori.FBXUVTextureTransfer
{
    [AddComponentMenu("Gokoukotori/FBX UV Texture Transfer/FBX UV Body Overlay Transfer Layer (β版)")]
    [DisallowMultipleComponent]
    public sealed class FBXUVBodyOverlayTransferLayer : MonoBehaviour, IExternalToolCanBehaveAsImageLayerV1, INDMFEditorOnly
    {
        internal const int MaxResolution = 2048;

        public GameObject sourceModelOrPrefab;
        public GameObject targetModelOrPrefab;
        public Texture2D overlayTexture;
        public Texture sourceBodyTexture;
        public bool alignNipples = true;
        public FBXUVBodyManualSettings manual = new FBXUVBodyManualSettings();
        public Shader bodyShader;
        public ComputeShader pixelProcessShader;

        [NonSerialized] private string lastLoggedReason;

        internal bool IsEnabledInHierarchy => enabled && gameObject.activeInHierarchy;
        public Shader ResolveBodyShader() => bodyShader != null ? bodyShader : Resources.Load<Shader>("FBXUVTextureTransfer/BodyOverlayTransfer");
        public ComputeShader ResolvePixelProcessShader() => pixelProcessShader != null ? pixelProcessShader : FBXUVTextureTransferResources.LoadPixelProcessShader();
        public bool HasExclusiveExternalToolImplementation() => FBXUVLayerUtility.HasExclusiveImplementation(this);
        public bool CanRender(out string reason) => TryPrepare(ResolveCanvasResolution(), out _, out reason);

        // R = min(max(Canvas width, height), 2048). The correspondence map is R x R in UV space for any canvas shape.
        internal static int ResolveResolution(int width, int height) => Mathf.Min(Mathf.Max(width, height), MaxResolution);

        // R for the canvas as the build evaluates it (validation, diagnostics and the build's LoadImage share its cache entry).
        // TexTransTool previews clamp the canvas to 1024, so a preview's LoadImage may use R = 1024 and, for a larger canvas, a
        // second cache entry for the same avatar pair.
        internal int ResolveCanvasResolution()
        {
            if (!FBXUVCanvasTargetUtility.TryFindCanvas(this, out var canvas, out _, out _)) return 0;
            var description = canvas.tttImportedCanvasDescription;
            if (description != null) return ResolveResolution(description.Width, description.Height);
            var texture = canvas.TargetTexture?.SelectTexture;
            return texture == null ? 0 : ResolveResolution(NormalizePowerOfTwo(texture.width), NormalizePowerOfTwo(texture.height));
        }

        // The correspondence with this layer's pins applied: what rendering and validation use. A pin that cannot be
        // applied stops the layer (spec §5.2).
        internal bool TryPrepare(int resolution, out FBXUVBodyCorrespondenceEntry entry, out string reason)
        {
            if (!TryPrepareCorrespondence(resolution, out entry, out reason)) return false;
            try
            {
                ApplyPins(entry);
                return true;
            }
            catch (Exception e)
            {
                entry = null;
                reason = e.Message;
                return false;
            }
        }

        // The shared correspondence of the avatar pair only, without applying pins (diagnostics and warnings). Its SourceUv /
        // Valid may carry whichever layer's pins were applied last; read Base* and Diagnostics.
        internal bool TryPrepareCorrespondence(int resolution, out FBXUVBodyCorrespondenceEntry entry, out string reason)
        {
            entry = null;
            try
            {
                var shader = ResolveBodyShader();
                if (!SystemInfo.supportsComputeShaders || shader == null || !shader.isSupported || ResolvePixelProcessShader() == null)
                    throw new ArgumentException("ボディ転送のShader / ComputeShaderを利用できません。");
                if (overlayTexture == null) throw new ArgumentException("オーバーレイ画像が未設定です。");
                if (sourceModelOrPrefab == null) throw new ArgumentException("転送元 Model/Prefab が未設定です。");
                if (targetModelOrPrefab == null) throw new ArgumentException("転送先 Model/Prefab が未設定です。");
                if (sourceBodyTexture == null) throw new ArgumentException("転送元ボディテクスチャが未設定です。");
                var targetTexture = FBXUVCanvasTargetUtility.ResolveTargetTexture(this);
                if (targetTexture == null) throw new ArgumentException("親 MultiLayerImageCanvas の Target Texture が見つかりません。");
                if (resolution <= 0) throw new ArgumentException("対応マップの解像度が不正です。");
                entry = FBXUVBodyCorrespondenceCache.Get(sourceModelOrPrefab, sourceBodyTexture, targetModelOrPrefab, targetTexture, resolution, alignNipples);
                reason = string.Empty;
                return true;
            }
            catch (Exception e)
            {
                entry = null;
                reason = e.Message;
                return false;
            }
        }

        // The entry is shared by every layer of this avatar pair and pins rewrite its map, so each layer applies its own pins
        // (or restores the base map) on every prepare; only the changed texels are uploaded again.
        private void ApplyPins(FBXUVBodyCorrespondenceEntry entry)
        {
            if (!TryApplyPins(entry, manual, out var pinReason)) throw new ArgumentException(pinReason);
        }

        // Applies the pin settings to the shared map (the base map when they are off or empty) and uploads the changed texels.
        // On false the map is unchanged. The body editor uses it for settings that are not saved yet (a drag draft).
        internal static bool TryApplyPins(FBXUVBodyCorrespondenceEntry entry, FBXUVBodyManualSettings manual, out string reason)
        {
            if (entry == null) throw new ArgumentNullException(nameof(entry));
            var correspondence = entry.Correspondence;
            RectInt changed;
            if (manual != null && manual.enabled && manual.pins != null && manual.pins.Count > 0)
            {
                if (!FBXUVBodyPinWarp.TryCreate(correspondence, manual.pins, out var warp, out reason)) return false;
                changed = warp.Apply(correspondence);
            }
            else
            {
                changed = FBXUVBodyPinWarp.Restore(correspondence);
                reason = string.Empty;
            }
            FBXUVBodyOverlayTransferRenderer.UploadRegion(entry, changed);
            return true;
        }

        public void LoadImage(RenderTexture writeDistentionTexture)
        {
            FBXUVLayerUtility.LoadImage(writeDistentionTexture, this, RenderIfReady,
                "FBX UV Body Overlay Transferの生成に失敗しました。透明レイヤーを返します。",
                "FBX UV Body Overlay Transferの透明fallback生成に失敗しました。");
        }

        private void RenderIfReady(RenderTexture destination)
        {
            if (!IsEnabledInHierarchy) return;
            if (!TryPrepare(ResolveResolution(destination.width, destination.height), out var entry, out var reason))
            {
                // Previews call LoadImage repeatedly; report each distinct reason once.
                if (reason != lastLoggedReason) Debug.LogWarning("FBX UV Body Overlay Transfer: 透明レイヤーを返します。" + reason, this);
                lastLoggedReason = reason;
                return;
            }
            lastLoggedReason = null;
            FBXUVBodyOverlayTransferRenderer.Render(this, destination, entry);
        }

        // Matches MultiLayerImageCanvas: a non-power-of-two size snaps to the closer power of two.
        private static int NormalizePowerOfTwo(int value)
        {
            if (Mathf.IsPowerOfTwo(value)) return value;
            var next = Mathf.NextPowerOfTwo(value);
            var closest = Mathf.ClosestPowerOfTwo(value);
            return Mathf.Abs(next - value) > Mathf.Abs(closest - value) ? closest : next;
        }

        private void Reset() => EnsureResources();
        private void OnValidate()
        {
            manual ??= new FBXUVBodyManualSettings();
            manual.pins ??= new List<FBXUVBodyPin>();
            EnsureResources();
        }
        private void EnsureResources()
        {
            FBXUVLayerUtility.EnsureExternalToolAsLayer(gameObject);
            if (bodyShader == null) bodyShader = ResolveBodyShader();
            if (pixelProcessShader == null) pixelProcessShader = ResolvePixelProcessShader();
        }
    }
}
