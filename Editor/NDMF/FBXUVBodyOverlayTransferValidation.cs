using System.Collections.Generic;
using UnityEngine;

namespace GokouKotori.FBXUVTextureTransfer.Editor.NDMF
{
    internal static class FBXUVBodyOverlayTransferValidation
    {
        // Errors only (spec 7.2). Warnings come from CollectWarnings.
        internal static IReadOnlyList<FBXUVTransferValidationIssue> ValidateLayer(
            FBXUVBodyOverlayTransferLayer layer, bool environment = true, GameObject buildRoot = null)
        {
            var issues = new List<FBXUVTransferValidationIssue>();
            void Add(string text) => issues.Add(new FBXUVTransferValidationIssue(text, layer));
            if (layer == null) { Add("Body Overlay Transfer Layerがありません。"); return issues; }
            if (environment) FBXUVTextureTransferValidation.ValidateEnvironment(issues, layer);
            var placement = FBXUVLayerValidation.ValidatePlacement(layer, "FBX UV Body Overlay Transfer Layer", issues);
            var texture = placement.TargetTexture;
            var sourceValid = FBXUVLayerValidation.TryResolveModelRoot(layer.sourceModelOrPrefab, "Source", buildRoot, layer, issues, out var source);
            var targetValid = FBXUVLayerValidation.TryResolveModelRoot(layer.targetModelOrPrefab, "Target", buildRoot, layer, issues, out var target);
            // Build clones may remap prefab self references. Resolve asset roots before reading base geometry.
            if (buildRoot != null && sourceValid && targetValid)
            { layer.sourceModelOrPrefab = source; layer.targetModelOrPrefab = target; }

            // These messages are the ones TryPrepare reports; they are added here so CanRender is not asked for them again.
            if (layer.overlayTexture == null) Add("オーバーレイ画像が未設定です。");
            if (layer.sourceBodyTexture == null) Add("転送元ボディテクスチャが未設定です。");
            else if (sourceValid && FBXUVTextureUsage.Collect(source, layer.sourceBodyTexture).Count == 0)
                Add("転送元ボディテクスチャは転送元モデルで使われていません。");
            if (targetValid && texture != null && FBXUVTextureUsage.Collect(target, texture).Count == 0)
                Add("Canvasの対象テクスチャを使うサブメッシュが転送先モデルにありません。");

            var sourceAnimator = sourceValid ? HumanoidAnimator(source) : null;
            var targetAnimator = targetValid ? HumanoidAnimator(target) : null;
            if (sourceValid && sourceAnimator == null) Add("Humanoid のアバターが必要です（Source）。");
            if (targetValid && targetAnimator == null) Add("Humanoid のアバターが必要です（Target）。");
            if (sourceAnimator != null && targetAnimator != null)
            {
                var missing = FBXUVBodySkeletonAlignment.MissingRequiredBones(Joints(sourceAnimator), Joints(targetAnimator));
                if (missing.Count > 0) Add("共通の骨が足りません: " + string.Join(", ", missing));
            }

            AddPinIssues(layer, Add);

            // Everything else is whatever TryPrepare reports: shader and correspondence build failures, and the pin errors
            // that need the correspondence (off-body, overlapping, flipping).
            if (issues.Count == 0 && !layer.CanRender(out var reason)) Add(reason);
            return issues;
        }

        internal static IReadOnlyList<string> CollectWarnings(FBXUVBodyOverlayTransferLayer layer)
        {
            if (layer == null) return new string[0];
            try
            {
                // The correspondence alone: a pin that cannot be applied is a ValidateLayer error, not a reason to hide warnings.
                if (!layer.TryPrepareCorrespondence(layer.ResolveCanvasResolution(), out var entry, out _) || entry?.Correspondence == null)
                    return new string[0];
                var warnings = new List<string>();
                if (entry.Correspondence.Diagnostics?.Warnings != null) warnings.AddRange(entry.Correspondence.Diagnostics.Warnings);
                if (layer.manual != null && layer.manual.enabled)
                    warnings.AddRange(FBXUVBodyPinWarp.SharedUvWarnings(entry.Correspondence, layer.manual.pins));
                return warnings;
            }
            catch (System.ArgumentException) { }
            return new string[0];
        }

        // Pin count and values need no correspondence. Off-body, overlapping and flipping pins need one; CanRender reports them.
        private static void AddPinIssues(FBXUVBodyOverlayTransferLayer layer, System.Action<string> add)
        {
            if (layer.manual == null || !layer.manual.enabled) return;
            var error = FBXUVBodyPinWarp.ValidateValues(layer.manual.pins);
            if (error != null) add(error);
        }

        private static Animator HumanoidAnimator(GameObject root)
        {
            var animator = root.GetComponent<Animator>();
            if (animator == null) animator = root.GetComponentInChildren<Animator>(true);
            return animator != null && animator.avatar != null && animator.avatar.isHuman ? animator : null;
        }

        private static IReadOnlyDictionary<int, Vector3> Joints(Animator animator)
        {
            var joints = new Dictionary<int, Vector3>();
            for (var bone = 0; bone < (int)HumanBodyBones.LastBone; bone++)
            {
                var transform = animator.GetBoneTransform((HumanBodyBones)bone);
                if (transform != null) joints.Add(bone, transform.position);
            }
            return joints;
        }
    }
}
