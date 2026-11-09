using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using GokouKotori.FBXUVTextureTransfer.Editor.NDMF;
using UnityEditor;
using UnityEngine;

namespace GokouKotori.FBXUVTextureTransfer.Editor
{
    [CustomEditor(typeof(FBXUVBodyOverlayTransferLayer))]
    internal sealed class FBXUVBodyOverlayTransferLayerEditor : FBXUVLayerEditor<FBXUVBodyOverlayTransferLayer>
    {
        private ShapeChoices sourceChoices;
        private ShapeChoices targetChoices;

        // The candidates and the automatic name of one (model, texture) pair. Rebuilt only when either instance changes: the
        // inspector repaints constantly and must not scan the whole prefab each time.
        private sealed class ShapeChoices
        {
            internal int ModelId, TextureId;
            internal IReadOnlyList<string> Names = new List<string>();
            internal string AutoName;
        }

        protected override void OnEnable()
        {
            base.OnEnable();
            var layer = Layer;
            if (layer.sourceBodyTexture == null && layer.sourceModelOrPrefab != null) ApplySuggestedSourceTexture(layer);
        }

        private protected override void ClearResults()
        {
            sourceChoices = null;
            targetChoices = null;
        }

        // Fills the source body texture with the model's most likely body texture. Keeps the current value when none is found.
        internal static void ApplySuggestedSourceTexture(FBXUVBodyOverlayTransferLayer layer)
        {
            if (layer == null || layer.sourceModelOrPrefab == null) return;
            var suggested = FBXUVBodyGeometry.SuggestBodyTexture(layer.sourceModelOrPrefab);
            if (suggested == null || suggested == layer.sourceBodyTexture) return;
            Undo.RecordObject(layer, "転送元ボディテクスチャを自動で選ぶ");
            layer.sourceBodyTexture = suggested;
            EditorUtility.SetDirty(layer);
            PrefabUtility.RecordPrefabInstancePropertyModifications(layer);
        }

        // "{Renderer path} sub{n}" for every submesh that uses the canvas target texture.
        internal static string[] DescribeTargetParts(FBXUVBodyOverlayTransferLayer layer)
        {
            var parts = new List<string>();
            var root = layer == null ? null : layer.targetModelOrPrefab;
            var texture = layer == null ? null : FBXUVCanvasTargetUtility.ResolveTargetTexture(layer);
            if (root == null || texture == null) return parts.ToArray();
            foreach (var usage in FBXUVTextureUsage.Collect(root, texture))
            {
                var path = RendererPath(root.transform, usage.Renderer.transform);
                foreach (var subMesh in usage.SubMeshIndices) parts.Add(path + " sub" + subMesh);
            }
            return parts.ToArray();
        }

        private static string RendererPath(Transform root, Transform transform)
        {
            var path = FBXUVRendererMeshUtility.RelativePath(root, transform);
            return path.Length == 0 ? transform.name : path;
        }

        private protected override bool HasRequiredResources()
            => Layer.ResolveBodyShader() != null && Layer.ResolvePixelProcessShader() != null;

        public override void OnInspectorGUI()
        {
            var layer = Layer;
            serializedObject.UpdateIfRequiredOrScript();
            DrawHeader("ボディ転送（β版）");
            var previousSource = layer.sourceModelOrPrefab;
            DrawModelFields();
            EditorGUILayout.PropertyField(serializedObject.FindProperty("overlayTexture"), new GUIContent("オーバーレイ画像"));
            var changed = serializedObject.ApplyModifiedProperties();
            // The source model drives the suggestion, so it is applied before the source body texture is drawn.
            if (layer.sourceModelOrPrefab != previousSource)
            {
                ApplySuggestedSourceTexture(layer);
                serializedObject.Update();
            }

            DrawSourceBodyTexture(layer);
            EditorGUILayout.PropertyField(
                serializedObject.FindProperty("alignNipples"),
                new GUIContent("乳首を目印に合わせる", "胸で辺が扇状に集まる点（乳首）や乳首用シェイプキーが動かす範囲を左右の目印として、転送元と転送先の乳首の位置と乳輪の大きさを合わせます。"));
            changed |= serializedObject.ApplyModifiedProperties();
            if (layer.alignNipples)
            {
                changed |= DrawShapePopup(layer, "転送元のシェイプキー", layer.sourceNippleShape, layer.sourceModelOrPrefab, layer.sourceBodyTexture, ref sourceChoices);
                changed |= DrawShapePopup(layer, "転送先のシェイプキー", layer.targetNippleShape, layer.targetModelOrPrefab,
                    FBXUVCanvasTargetUtility.ResolveTargetTexture(layer), ref targetChoices);
                serializedObject.Update();
            }

            DrawTargetParts(layer);
            // The pins are enabled and reset in the body transfer editor; the inspector only counts them.
            EditorGUILayout.LabelField("手補正のピン", (layer.manual?.pins == null ? 0 : layer.manual.pins.Count).ToString());
            EditorGUILayout.Space(4f);
            if (GUILayout.Button(FBXUVEditorNames.OpenButton(FBXUVEditorNames.BodyTransferEditor))) FBXUVBodyOverlayTransferWindow.Open(layer);
            DrawDiagnostics();
            if (changed) InvalidateResults();
            DrawValidation();
        }

        private protected override IReadOnlyList<FBXUVTransferValidationIssue> Validate()
            => WithProgressBar(() => FBXUVBodyOverlayTransferValidation.ValidateLayer(Layer));

        // Auto ("自動（name）" / "自動（見つかりません）"), None ("使わない"), then every shape key of the body's meshes.
        private bool DrawShapePopup(
            FBXUVBodyOverlayTransferLayer layer,
            string label,
            FBXUVBodyNippleShapeSelection selection,
            GameObject model,
            Texture texture,
            ref ShapeChoices choices)
        {
            if (selection == null) return false;
            var modelId = model == null ? 0 : model.GetInstanceID();
            var textureId = texture == null ? 0 : texture.GetInstanceID();
            if (choices == null || choices.ModelId != modelId || choices.TextureId != textureId)
            {
                choices = new ShapeChoices { ModelId = modelId, TextureId = textureId };
                if (model != null && texture != null)
                {
                    var usages = FBXUVTextureUsage.Collect(model, texture);
                    choices.Names = FBXUVBodyNippleShape.CandidateNames(usages);
                    choices.AutoName = FBXUVBodyNippleShape.AutoName(usages);
                }
            }

            var labels = new List<string>
            {
                choices.AutoName == null ? "自動（見つかりません）" : "自動（" + choices.AutoName + "）",
                "使わない"
            };
            labels.AddRange(choices.Names);
            var index = 0;
            if (selection.mode == FBXUVBodyNippleShapeMode.None) index = 1;
            else if (selection.mode == FBXUVBodyNippleShapeMode.Named)
            {
                var found = -1;
                for (var i = 0; i < choices.Names.Count; i++)
                {
                    if (choices.Names[i] == selection.shapeName) found = i;
                }
                if (found < 0)
                {
                    // A saved name this model does not have: keep it visible so the choice is not silently changed.
                    labels.Add(selection.shapeName + "（見つかりません）");
                    found = choices.Names.Count;
                }
                index = 2 + found;
            }

            EditorGUI.BeginChangeCheck();
            var picked = EditorGUILayout.Popup(label, index, labels.ToArray());
            if (!EditorGUI.EndChangeCheck() || picked == index) return false;
            Undo.RecordObject(layer, label + "を変更");
            if (picked == 0) selection.mode = FBXUVBodyNippleShapeMode.Auto;
            else if (picked == 1) selection.mode = FBXUVBodyNippleShapeMode.None;
            else if (picked - 2 < choices.Names.Count)
            {
                selection.mode = FBXUVBodyNippleShapeMode.Named;
                selection.shapeName = choices.Names[picked - 2];
            }
            else return false;
            EditorUtility.SetDirty(layer);
            PrefabUtility.RecordPrefabInstancePropertyModifications(layer);
            return true;
        }

        private void DrawSourceBodyTexture(FBXUVBodyOverlayTransferLayer layer)
        {
            var candidates = layer.sourceModelOrPrefab == null
                ? new List<Texture>()
                : FBXUVBodyGeometry.CandidateBodyTextures(layer.sourceModelOrPrefab);
            var current = layer.sourceBodyTexture;
            var options = new List<Texture>(candidates);
            if (current != null && !options.Contains(current)) options.Add(current);
            var labels = new List<string> { "（未設定）" };
            labels.AddRange(options.Select((texture, index) => index + ": " + texture.name));
            var selectedIndex = current == null ? 0 : options.IndexOf(current) + 1;

            EditorGUILayout.BeginHorizontal();
            EditorGUI.BeginChangeCheck();
            var picked = EditorGUILayout.Popup("転送元ボディテクスチャ", selectedIndex, labels.ToArray());
            if (EditorGUI.EndChangeCheck() && picked != selectedIndex)
            {
                Undo.RecordObject(layer, "転送元ボディテクスチャを変更");
                layer.sourceBodyTexture = picked == 0 ? null : options[picked - 1];
                EditorUtility.SetDirty(layer);
                PrefabUtility.RecordPrefabInstancePropertyModifications(layer);
                InvalidateResults();
            }
            using (new EditorGUI.DisabledScope(layer.sourceModelOrPrefab == null))
            {
                if (GUILayout.Button("自動で選ぶ", GUILayout.Width(80f)))
                {
                    ApplySuggestedSourceTexture(layer);
                    InvalidateResults();
                }
            }
            EditorGUILayout.EndHorizontal();
            serializedObject.Update();
        }

        private void DrawTargetParts(FBXUVBodyOverlayTransferLayer layer)
        {
            FBXUVLayerInspectorGUI.DrawCanvasTarget(layer);
            var parts = DescribeTargetParts(layer);
            EditorGUILayout.LabelField("転送先のRenderer・サブメッシュ", EditorStyles.miniBoldLabel);
            if (parts.Length == 0)
            {
                EditorGUILayout.HelpBox("Canvasの対象テクスチャを使うサブメッシュが転送先 Model / Prefabにありません。", MessageType.Warning);
                return;
            }
            using (new EditorGUI.DisabledScope(true))
            {
                foreach (var part in parts) EditorGUILayout.TextField(part);
            }
        }

        private protected override FBXUVMessage Diagnose()
            => WithProgressBar(() =>
            {
                var lines = BuildDiagnostics(Layer);
                var text = string.Join("\n", lines);
                return lines.Count > 0 && lines[0].StartsWith(DiagnosisUnavailablePrefix, System.StringComparison.Ordinal)
                    ? FBXUVMessage.Warning(text)
                    : FBXUVMessage.Info(text);
            });

        // First-line prefix of the "diagnosis could not be made" result; Diagnose() shows it as a Warning.
        internal const string DiagnosisUnavailablePrefix = "診断できません: ";

        internal static List<string> BuildDiagnostics(FBXUVBodyOverlayTransferLayer layer)
        {
            var lines = new List<string>();
            // The correspondence only: a bad pin is a validation error and must not hide the diagnosis of the avatar pair.
            if (!layer.TryPrepareCorrespondence(layer.ResolveCanvasResolution(), out var entry, out var reason))
            {
                lines.Add(DiagnosisUnavailablePrefix + reason);
                return lines;
            }
            var diagnostics = entry.Correspondence.Diagnostics;
            if (diagnostics != null) lines.Add("見つかった割合: " + diagnostics.FoundRate.ToString("P1"));
            var landmarkLine = DescribeLandmarks(entry.Correspondence.Landmarks);
            if (landmarkLine != null) lines.Add(landmarkLine);
            var shapeLine = DescribeNippleShapes(entry.Correspondence.Landmarks);
            if (shapeLine != null) lines.Add(shapeLine);
            var regionLine = DescribeRegionTransfer(entry.Correspondence.Landmarks);
            if (regionLine != null) lines.Add(regionLine);
            var warningLines = new List<string>();
            if (diagnostics?.ByBone != null)
            {
                foreach (var bone in diagnostics.ByBone.Where(b => b.FoundRate < FBXUVBodyDiagnostics.FoundRateThreshold).OrderBy(b => b.FoundRate))
                    warningLines.Add("低い部位: " + bone.Bone + " " + bone.FoundRate.ToString("P1") + "（" + bone.Covered + "テクセル）");
            }
            warningLines.AddRange(FBXUVBodyOverlayTransferValidation.CollectWarnings(layer));
            if (warningLines.Count == 0) warningLines.Add("警告はありません。");
            lines.AddRange(warningLines);
            return lines;
        }

        // The nipple landmark line of the diagnostics; null when there is no result.
        internal static string DescribeLandmarks(FBXUVBodyLandmarkResult result)
        {
            if (result == null) return null;
            if (!result.Used || result.Left == null || result.Right == null)
                return "乳首の目印: 使っていません（" + result.Reason + "）";
            return "乳首の目印: 使用（ずれ 左 " + Millimeters(result.Left.OffsetBefore) + " → " + Millimeters(result.Left.OffsetAfter)
                   + " mm、右 " + Millimeters(result.Right.OffsetBefore) + " → " + Millimeters(result.Right.OffsetAfter)
                   + " mm、" + DescribeSize(result) + "、身長 1 m 換算）";
        }

        // 大きさ 0.91 倍（転送元 11.9 mm → 転送先 10.8 mm）, or why the size was left alone. The left side's values stand for both.
        private static string DescribeSize(FBXUVBodyLandmarkResult result)
        {
            if (!result.ScaleUsed)
                return "大きさ: 合わせていません" + (string.IsNullOrEmpty(result.ScaleReason) ? "" : "（" + result.ScaleReason + "）");
            var left = result.Left;
            var scale = float.IsNaN(left.Scale) ? "—" : left.Scale.ToString("0.00", CultureInfo.InvariantCulture);
            return "大きさ " + scale + " 倍（転送元 " + Millimeters(left.SourceAlignedRadius) + " mm → 転送先 " + Millimeters(left.TargetRadius) + " mm）";
        }

        // The shape key line of the diagnostics; null when the landmarks are off or there is no result.
        internal static string DescribeNippleShapes(FBXUVBodyLandmarkResult result)
        {
            if (result == null || !result.Enabled) return null;
            return "乳首のシェイプキー: 転送元 " + DescribeShape(result.SourceShape, result.SourceShapeAuto, result.SourceShapeReason)
                   + "、転送先 " + DescribeShape(result.TargetShape, result.TargetShapeAuto, result.TargetShapeReason);
        }

        // The region copy line of the diagnostics; null when the landmarks are off or there is nothing to report
        // (no usable region on both avatars: neither a copy nor a reason).
        internal static string DescribeRegionTransfer(FBXUVBodyLandmarkResult result)
        {
            if (result == null || !result.Enabled) return null;
            if (result.RegionTransferUsed)
            {
                return "乳首の範囲: 範囲どうしで写しました（左 " + (result.Left?.RegionTexels ?? 0).ToString(CultureInfo.InvariantCulture)
                       + " 画素、右 " + (result.Right?.RegionTexels ?? 0).ToString(CultureInfo.InvariantCulture) + " 画素）";
            }
            if (result.RegionTransferReason == null) return null;
            return "乳首の範囲: 範囲どうしでは写していません（" + result.RegionTransferReason + "）";
        }

        // "{name|なし}{（自動）}{（reason）}": （自動） also follows なし when the automatic search found nothing.
        private static string DescribeShape(string name, bool auto, string reason)
            => (string.IsNullOrEmpty(name) ? "なし" : name) + (auto ? "（自動）" : "")
               + (string.IsNullOrEmpty(reason) ? "" : "（" + reason + "）");

        private static string Millimeters(float meters)
            => float.IsNaN(meters) ? "—" : (meters * 1000f).ToString("0.0", CultureInfo.InvariantCulture);

        // Both buttons use the canvas resolution (up to 2048), which the editor and the preview (1024) may not have prepared:
        // the correspondence can take seconds then, as in the body editor.
        private static T WithProgressBar<T>(System.Func<T> work)
        {
            try
            {
                EditorUtility.DisplayProgressBar("ボディ転送", "対応づけを計算しています…", .5f);
                return work();
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
        }
    }
}
