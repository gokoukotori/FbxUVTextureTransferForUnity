using System;
using System.Collections.Generic;
using nadena.dev.ndmf;
using net.rs64.TexTransTool.MultiLayerImage;
using UnityEngine;

namespace GokouKotori.FBXUVTextureTransfer
{
    [AddComponentMenu("FBX UV Texture Transfer/FBX UV Makeup Transfer Layer (β版)")]
    [DisallowMultipleComponent]
    public sealed class FBXUVMakeupTransferLayer : MonoBehaviour, IExternalToolCanBehaveAsImageLayerV1, INDMFEditorOnly
    {
        public GameObject sourceModelOrPrefab;
        public GameObject targetModelOrPrefab;
        public Texture2D makeupTexture;
        public Texture2D sourceReferenceTexture;
        public FBXUVTransferRegion sourceRegion = new FBXUVTransferRegion();
        public FBXUVTransferRegion targetRegion = new FBXUVTransferRegion();
        public List<FBXUVMakeupLandmark> landmarks = new List<FBXUVMakeupLandmark>();
        [Range(0f, .1f)] public float warpSmoothing;

        public bool IsEnabledInHierarchy => enabled && gameObject.activeInHierarchy;
        public bool UsesExactMapping => true;

        [NonSerialized] private FBXUVMakeupComputationCache computationCache;
        internal FBXUVMakeupComputationCache ComputationCache =>
            computationCache ?? (computationCache = new FBXUVMakeupComputationCache());
        internal FBXUVMakeupMapping GetMapping() => ComputationCache.GetMapping(this);

        public Shader ResolveTransferShader()
        {
            return Resources.Load<Shader>("FBXUVTextureTransfer/MakeupTransfer");
        }

        public bool CanRender(out string reason)
        {
            return CanRender(out reason, out _);
        }

        private bool CanRender(out string reason, out FBXUVMakeupMapping mapping)
        {
            mapping = null;
            if (makeupTexture == null) return Fail("メイクの透過PNGが未設定です。", out reason);
            var shader = ResolveTransferShader();
            if (shader == null || !shader.isSupported) return Fail("メイク転送Shaderを利用できません。", out reason);
            var cache = new FBXUVMeshAnalysisCache();
            try
            {
                if (!IsRegionReady(sourceRegion, cache) || !IsRegionReady(targetRegion, cache))
                    return Fail("顔Regionの選択・UV・Mesh hashが無効です。顔領域を再選択してください。", out reason);
            }
            catch (Exception)
            {
                return Fail("顔MeshのUV・三角形を読み取れません。顔領域を再選択してください。", out reason);
            }
            try { mapping = GetMapping(); }
            catch (Exception exception) { return Fail("基本変形を生成できません。" + exception.Message, out reason); }
            reason = string.Empty;
            return true;
        }

        public bool TryCreateWarp(out FBXUVMakeupWarp warp, out string reason)
        {
            return FBXUVMakeupWarp.TryCreate(landmarks, warpSmoothing, out warp, out reason);
        }

        public bool HasExclusiveExternalToolImplementation()
        {
            var count = 0;
            foreach (var component in GetComponents<MonoBehaviour>())
                if (component is IExternalToolCanBehaveAsLayer) count++;
            return count == 1;
        }

        public void LoadImage(RenderTexture writeDistentionTexture)
        {
            if (writeDistentionTexture == null) return;
            try
            {
                FBXUVTextureTransferRenderer.Clear(writeDistentionTexture);
                string reason;
                if (!IsEnabledInHierarchy || !CanRender(out reason, out var mapping)) return;
                FBXUVMakeupTransferRenderer.Render(this, writeDistentionTexture, mapping);
            }
            catch (Exception exception)
            {
                try { FBXUVTextureTransferRenderer.Clear(writeDistentionTexture); }
                catch (Exception clearException) { Debug.LogError("メイク転送の透明fallback生成に失敗しました。\n" + clearException, this); }
                Debug.LogError("メイク転送に失敗しました。透明レイヤーを返します。\n" + exception, this);
            }
        }

        private static bool IsRegionReady(FBXUVTransferRegion region, FBXUVMeshAnalysisCache cache)
        {
            if (region == null || !region.bounds.IsValid || region.triangles == null || region.triangles.Count == 0
                || !FBXUVMakeupWarp.IsUnitUv(new Vector2(region.bounds.minU, region.bounds.minV))
                || !FBXUVMakeupWarp.IsUnitUv(new Vector2(region.bounds.maxU, region.bounds.maxV))
                || !FBXUVMeshUtility.IsMeshHashCurrent(region, cache)) return false;
            var hasArea = false;
            var meshTriangles = cache.Get(region.mesh, region.subMeshIndex, region.uvChannel).GetTriangles();
            var seen = new HashSet<int>();
            foreach (var triangle in region.triangles)
            {
                if (triangle == null || triangle.index < 0 || triangle.index >= meshTriangles.Count || !seen.Add(triangle.index)) return false;
                var original = meshTriangles[triangle.index];
                for (var i = 0; i < 3; i++)
                    if (!FBXUVMakeupWarp.IsUnitUv(triangle.GetPoint(i)) || !region.bounds.Contains(triangle.GetPoint(i))
                        || (triangle.GetPoint(i) - original.GetPoint(i)).sqrMagnitude > 1e-12f) return false;
                var ab = triangle.b - triangle.a;
                var ac = triangle.c - triangle.a;
                if (Math.Abs(ab.x * (double)ac.y - ab.y * (double)ac.x) > 1e-12) hasArea = true;
            }
            return hasArea;
        }

        private static bool Fail(string message, out string reason) { reason = message; return false; }
        private void Reset() { EnsureExternalToolAsLayer(); }
        private void OnValidate()
        {
            if (landmarks == null) landmarks = new List<FBXUVMakeupLandmark>();
            EnsureExternalToolAsLayer();
        }
        private void EnsureExternalToolAsLayer()
        {
            if (GetComponent<ExternalToolAsLayer>() == null) gameObject.AddComponent<ExternalToolAsLayer>();
        }
    }
}
