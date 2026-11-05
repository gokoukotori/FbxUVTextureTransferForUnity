using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using GokouKotori.FBXUVTextureTransfer.Editor.NDMF;
using UnityEditor;
using UnityEngine;

namespace GokouKotori.FBXUVTextureTransfer.Editor
{
    [CustomEditor(typeof(FBXUVBodyOverlayTransferLayer))]
    internal sealed class FBXUVBodyOverlayTransferLayerEditor : UnityEditor.Editor
    {
        private string targetAutoResolutionError;
        private string diagnosticsState;
        private List<string> diagnosticsLines;
        private IReadOnlyList<FBXUVTransferValidationIssue> lastValidationIssues;
        private string validationState;

        private void OnEnable()
        {
            var layer = (FBXUVBodyOverlayTransferLayer)target;
            FBXUVModelPrefabReferenceUtility.TryPopulateTargetRoot(layer, out _, out targetAutoResolutionError);
            if (layer.sourceBodyTexture == null && layer.sourceModelOrPrefab != null) ApplySuggestedSourceTexture(layer);
            Undo.undoRedoPerformed += InvalidateResults;
            EditorApplication.projectChanged += InvalidateResults;
        }

        private void OnDisable()
        {
            Undo.undoRedoPerformed -= InvalidateResults;
            EditorApplication.projectChanged -= InvalidateResults;
        }

        private void InvalidateResults()
        {
            diagnosticsState = null;
            diagnosticsLines = null;
            validationState = null;
            lastValidationIssues = null;
            Repaint();
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
            var names = new List<string>();
            for (var current = transform; current != null && current != root; current = current.parent) names.Add(current.name);
            names.Reverse();
            return names.Count == 0 ? transform.name : string.Join("/", names);
        }

        public override void OnInspectorGUI()
        {
            var layer = (FBXUVBodyOverlayTransferLayer)target;
            serializedObject.UpdateIfRequiredOrScript();
            EditorGUILayout.LabelField("ボディ転送（β版）", EditorStyles.boldLabel);
            var previousSource = layer.sourceModelOrPrefab;
            FBXUVLayerInspectorGUI.DrawModelFields(serializedObject, ref targetAutoResolutionError);
            EditorGUILayout.PropertyField(serializedObject.FindProperty("overlayTexture"), new GUIContent("オーバーレイ画像"));
            EditorGUILayout.PropertyField(
                serializedObject.FindProperty("alignNipples"),
                new GUIContent("乳首を目印に合わせる", "胸で辺が扇状に集まる点（乳首）を左右の目印として、転送元と転送先の位置を合わせます。"));
            var changed = serializedObject.ApplyModifiedProperties();
            if (layer.sourceModelOrPrefab != previousSource)
            {
                ApplySuggestedSourceTexture(layer);
                serializedObject.Update();
            }

            DrawSourceBodyTexture(layer);
            DrawTargetParts(layer);
            DrawDiagnostics(layer);
            DrawManual(layer);
            if (changed) InvalidateResults();
            EditorGUILayout.Space(4f);
            if (GUILayout.Button("ボディ転送エディターを開く")) FBXUVBodyOverlayTransferWindow.Open(layer);
            DrawValidation(layer);
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
                EditorGUILayout.HelpBox("Canvasの対象テクスチャを使うサブメッシュが転送先モデルにありません。", MessageType.Warning);
                return;
            }
            using (new EditorGUI.DisabledScope(true))
            {
                foreach (var part in parts) EditorGUILayout.TextField(part);
            }
        }

        private void DrawDiagnostics(FBXUVBodyOverlayTransferLayer layer)
        {
            EditorGUILayout.Space(4f);
            // A cached result must never survive a change of the inputs.
            if (diagnosticsState != null && diagnosticsState != EditorJsonUtility.ToJson(layer)) InvalidateResults();
            if (GUILayout.Button("診断"))
            {
                diagnosticsLines = WithProgressBar(() => BuildDiagnostics(layer));
                diagnosticsState = EditorJsonUtility.ToJson(layer);
            }
            if (diagnosticsLines == null) return;
            EditorGUILayout.HelpBox(string.Join("\n", diagnosticsLines), MessageType.Info);
        }

        internal static List<string> BuildDiagnostics(FBXUVBodyOverlayTransferLayer layer)
        {
            var lines = new List<string>();
            // The correspondence only: a bad pin is a validation error and must not hide the diagnosis of the avatar pair.
            if (!layer.TryPrepareCorrespondence(layer.ResolveCanvasResolution(), out var entry, out var reason))
            {
                lines.Add("診断できません: " + reason);
                return lines;
            }
            var diagnostics = entry.Correspondence.Diagnostics;
            if (diagnostics != null) lines.Add("見つかった割合: " + diagnostics.FoundRate.ToString("P1"));
            var landmarkLine = DescribeLandmarks(entry.Correspondence.Landmarks);
            if (landmarkLine != null) lines.Add(landmarkLine);
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
                   + " mm、身長 1 m 換算）";
        }

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

        private void DrawManual(FBXUVBodyOverlayTransferLayer layer)
        {
            EditorGUILayout.Space(4f);
            EditorGUILayout.LabelField("補正ピン", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(serializedObject.FindProperty("manual.enabled"), new GUIContent("補正を有効にする"));
            if (serializedObject.ApplyModifiedProperties()) InvalidateResults();
            var count = layer.manual?.pins == null ? 0 : layer.manual.pins.Count;
            EditorGUILayout.LabelField("ピン数", count.ToString());
            using (new EditorGUI.DisabledScope(count == 0))
            {
                if (GUILayout.Button("すべてリセット") &&
                    EditorUtility.DisplayDialog("補正ピンのリセット", "すべての補正ピンを削除します。よろしいですか？", "リセット", "キャンセル"))
                {
                    Undo.RecordObject(layer, "補正ピンをすべてリセット");
                    layer.manual.pins.Clear();
                    EditorUtility.SetDirty(layer);
                    PrefabUtility.RecordPrefabInstancePropertyModifications(layer);
                    InvalidateResults();
                    serializedObject.Update();
                }
            }
        }

        private void DrawValidation(FBXUVBodyOverlayTransferLayer layer)
        {
            EditorGUILayout.Space(4f);
            if (validationState != null && validationState != EditorJsonUtility.ToJson(layer)) InvalidateResults();
            if (GUILayout.Button("現在の設定を検証"))
            {
                lastValidationIssues = WithProgressBar(() => FBXUVBodyOverlayTransferValidation.ValidateLayer(layer));
                validationState = EditorJsonUtility.ToJson(layer);
            }
            if (lastValidationIssues == null) return;
            if (lastValidationIssues.Count == 0)
            {
                EditorGUILayout.HelpBox("検証エラーはありません。", MessageType.Info);
                return;
            }
            foreach (var issue in lastValidationIssues) EditorGUILayout.HelpBox(issue.Message, MessageType.Error);
        }
    }
}
