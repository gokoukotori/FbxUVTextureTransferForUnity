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
            if (!FBXUVLayerValidation.TryBegin(layer, "FBX UV Body Overlay Transfer Layer がありません。", "FBX UV Body Overlay Transfer Layer",
                    environment, buildRoot, useResolvedRootsForBuild: true, out var checks))
                return checks.Issues;
            var texture = checks.TargetTexture;
            var sourceValid = checks.SourceValid;
            var source = checks.SourceRoot;
            var targetValid = checks.TargetValid;
            var target = checks.TargetRoot;

            // These messages are the ones TryPrepare reports; they are added here so CanRender is not asked for them again.
            if (layer.overlayTexture == null) checks.Add("オーバーレイ画像が未設定です。");
            if (layer.sourceBodyTexture == null) checks.Add("転送元ボディテクスチャが未設定です。");
            else if (sourceValid && FBXUVTextureUsage.Collect(source, layer.sourceBodyTexture).Count == 0)
                checks.Add("転送元ボディテクスチャは転送元 Model / Prefabで使われていません。");
            if (targetValid && texture != null && FBXUVTextureUsage.Collect(target, texture).Count == 0)
                checks.Add("Canvasの対象テクスチャを使うサブメッシュが転送先 Model / Prefabにありません。");

            var sourceAnimator = sourceValid ? HumanoidAnimator(source) : null;
            var targetAnimator = targetValid ? HumanoidAnimator(target) : null;
            if (sourceValid && sourceAnimator == null) checks.Add("Humanoid のアバターが必要です（転送元）。");
            if (targetValid && targetAnimator == null) checks.Add("Humanoid のアバターが必要です（転送先）。");
            if (sourceAnimator != null && targetAnimator != null)
            {
                var missing = FBXUVBodySkeletonAlignment.MissingRequiredBones(Joints(sourceAnimator), Joints(targetAnimator));
                if (missing.Count > 0) checks.Add("共通の骨が足りません: " + string.Join(", ", missing));
            }

            AddPinIssues(layer, checks.Add);

            // Everything else is whatever TryPrepare reports: shader and correspondence build failures, and the pin errors
            // that need the correspondence (off-body, overlapping, flipping).
            if (checks.Issues.Count == 0 && !layer.CanRender(out var reason)) checks.Add(reason);
            return checks.Issues;
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
