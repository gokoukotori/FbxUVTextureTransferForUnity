using GokouKotori.FBXUVTextureTransfer.Editor.NDMF;
using UnityEditor;
using UnityEngine;

namespace GokouKotori.FBXUVTextureTransfer.Editor
{
    [CustomEditor(typeof(FBXUVMakeupTransferLayer))]
    public sealed class FBXUVMakeupTransferLayerEditor : UnityEditor.Editor
    {
        private string inspectionState;
        private string inspectionMessage;
        private MessageType inspectionMessageType;
        private string targetAutoResolutionError;

        private void OnEnable()
        {
            FBXUVModelPrefabReferenceUtility.TryPopulateTargetRoot(
                (FBXUVMakeupTransferLayer)target, out _, out targetAutoResolutionError);
            Undo.undoRedoPerformed += InvalidateInspection;
            EditorApplication.projectChanged += InvalidateInspection;
        }

        private void OnDisable()
        {
            Undo.undoRedoPerformed -= InvalidateInspection;
            EditorApplication.projectChanged -= InvalidateInspection;
        }

        private void InvalidateInspection()
        {
            inspectionState = null;
            inspectionMessage = null;
            Repaint();
            FBXUVMakeupTransferWindow.RepaintFor(target as FBXUVMakeupTransferLayer);
        }

        public override void OnInspectorGUI()
        {
            serializedObject.UpdateIfRequiredOrScript();
            EditorGUILayout.LabelField("メイク転送（β版）", EditorStyles.boldLabel);
            DrawModel("sourceModelOrPrefab", "転送元 Model / Prefab");
            DrawModel("targetModelOrPrefab", "転送先 Model / Prefab");
            if (serializedObject.FindProperty("targetModelOrPrefab").objectReferenceValue != null)
                targetAutoResolutionError = string.Empty;
            if (!string.IsNullOrEmpty(targetAutoResolutionError))
                EditorGUILayout.HelpBox($"転送先を自動決定できません: {targetAutoResolutionError}", MessageType.Warning);
            EditorGUILayout.PropertyField(serializedObject.FindProperty("makeupTexture"), new GUIContent("メイクの透過PNG"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("sourceReferenceTexture"), new GUIContent("元の顔画像（編集時の背景・任意）"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("warpSmoothing"), new GUIContent("変形の安定化係数", "同じ三角形補間の対応位置を正則化します。0は正則化なし。値を上げると対応点からのずれを許容します。口境界の自動補正は係数によらず有効です。"));
            if (serializedObject.ApplyModifiedProperties()) InvalidateInspection();
            var layer = (FBXUVMakeupTransferLayer)target;
            EditorGUILayout.HelpBox("透過PNGのメイクを目・口などの対応点に合わせて転送します。メイクだけの新規Canvasは、MLIC自体のBlendをNormalにすると元の肌に重ねられます。既存Canvasでは下地レイヤーの構成を確認してください。初期配置後はUVとアバター表示で確認してください。", MessageType.Info);
            EditorGUILayout.LabelField("対応点", (layer.landmarks == null ? 0 : layer.landmarks.Count).ToString());
            if (GUILayout.Button("メイク転送エディターを開く")) FBXUVMakeupTransferWindow.Open(layer);
            DrawWarpInspection(layer);
            if (FBXUVCanvasHierarchyUtility.TryFindCanvas(layer, out var canvas, out var error, out _))
            {
                using (new EditorGUI.DisabledScope(true))
                    EditorGUILayout.ObjectField("Canvasの対象テクスチャ", canvas.TargetTexture?.SelectTexture, typeof(Texture2D), false);
            }
            else EditorGUILayout.HelpBox(error, MessageType.Error);
            foreach (var issue in FBXUVMakeupTransferValidation.ValidateLayer(layer))
                EditorGUILayout.HelpBox(issue.Message, MessageType.Error);
        }

        private void DrawWarpInspection(FBXUVMakeupTransferLayer layer)
        {
            // Compare the actual serialized inputs, including edits in the separate window.
            // A cached inspection must never survive a landmark, region, or smoothing change.
            if (inspectionState != null && inspectionState != EditorJsonUtility.ToJson(layer))
                InvalidateInspection();
            EditorGUILayout.HelpBox("係数によらず三角形補間を使い、取得できた口のUV境界を自動で合わせます。口周辺では保存された目印より実際の境界を優先します。自動補正が成立しない場合は対応点補間を維持します。係数は対応位置の正則化の強さだけを調整します。", MessageType.Info);
            if (GUILayout.Button("基本対応を検査"))
            {
                try
                {
                    var mapping = layer.GetMapping();
                    var maximumError = 0f;
                    foreach (var point in layer.landmarks)
                        maximumError = Mathf.Max(maximumError, Vector2.Distance(mapping.Evaluate(point.targetUv), point.sourceUv));
                    inspectionMessage = "三角形補間: " + mapping.Exact.TriangleCount + "三角形。局所反転・重なりなし。最終表示は別途確認してください。"
                        + "\n" + mapping.BoundaryReason
                        + "\n対応点の最大ずれ: " + maximumError.ToString("G6") + " 転送元UV";
                    inspectionMessageType = MessageType.Info;
                }
                catch (System.Exception exception) { inspectionMessage = "検査できません: " + exception.Message; inspectionMessageType = MessageType.Warning; }
                inspectionState = EditorJsonUtility.ToJson(layer);
            }
            EditorGUILayout.HelpBox("形状や色の自然さはアバター表示でも確認してください。", MessageType.None);
            if (!string.IsNullOrEmpty(inspectionMessage))
                EditorGUILayout.HelpBox(inspectionMessage, inspectionMessageType);
        }

        private void DrawModel(string propertyName, string label)
        {
            var property = serializedObject.FindProperty(propertyName);
            property.objectReferenceValue = EditorGUILayout.ObjectField(label, property.objectReferenceValue, typeof(GameObject), false);
            if (!FBXUVModelPrefabReferenceUtility.TryResolveRoot(property.objectReferenceValue as GameObject, out _, out var error))
                EditorGUILayout.HelpBox(error, MessageType.Error);
        }
    }
}
