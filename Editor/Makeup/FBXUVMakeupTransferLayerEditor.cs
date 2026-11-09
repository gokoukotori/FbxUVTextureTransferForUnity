using System.Collections.Generic;
using GokouKotori.FBXUVTextureTransfer.Editor.NDMF;
using UnityEditor;
using UnityEngine;

namespace GokouKotori.FBXUVTextureTransfer.Editor
{
    [CustomEditor(typeof(FBXUVMakeupTransferLayer))]
    public sealed class FBXUVMakeupTransferLayerEditor : FBXUVLayerEditor<FBXUVMakeupTransferLayer>
    {
        private protected override void ClearResults()
        {
            FBXUVMakeupTransferWindow.RepaintFor(Layer);
        }

        private protected override bool HasRequiredResources()
            => Layer.ResolveTransferShader() != null;

        public override void OnInspectorGUI()
        {
            serializedObject.UpdateIfRequiredOrScript();
            DrawHeader("メイク転送（β版）");
            DrawModelFields();
            EditorGUILayout.PropertyField(serializedObject.FindProperty("makeupTexture"), new GUIContent("メイクの透過PNG"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("sourceReferenceTexture"), new GUIContent("元の顔画像（編集時の背景・任意）"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("warpSmoothing"), new GUIContent("変形の安定化係数", "同じ三角形補間の対応位置を正則化します。0は正則化なし。値を上げると対応点からのずれを許容します。口境界の自動補正は係数によらず有効です。"));
            if (serializedObject.ApplyModifiedProperties()) InvalidateResults();
            var layer = Layer;
            FBXUVLayerInspectorGUI.DrawCanvasTarget(layer);
            EditorGUILayout.HelpBox("透過PNGのメイクを目・口などの対応点に合わせて転送します。メイクだけの新規Canvasは、MultiLayerImageCanvas自体のBlendをNormalにすると元の肌に重ねられます。既存Canvasでは下地レイヤーの構成を確認してください。初期配置後はUVとアバター表示で確認してください。", MessageType.Info);
            EditorGUILayout.LabelField("対応点", (layer.landmarks == null ? 0 : layer.landmarks.Count).ToString());
            if (GUILayout.Button(FBXUVEditorNames.OpenButton(FBXUVEditorNames.MakeupTransferEditor))) FBXUVMakeupTransferWindow.Open(layer);
            DrawWarpInspection();
            DrawValidation();
        }

        private protected override IReadOnlyList<FBXUVTransferValidationIssue> Validate()
            => FBXUVMakeupTransferValidation.ValidateLayer(Layer);

        private void DrawWarpInspection()
        {
            EditorGUILayout.HelpBox("係数によらず三角形補間を使い、取得できた口のUV境界を自動で合わせます。口周辺では保存された目印より実際の境界を優先します。自動補正が成立しない場合は対応点補間を維持します。係数は対応位置の正則化の強さだけを調整します。", MessageType.Info);
            EditorGUILayout.HelpBox("形状や色の自然さはアバター表示でも確認してください。", MessageType.None);
            DrawDiagnostics();
        }

        // The mapping inspection: the triangle count, the boundary handling and how far the points drift. A mapping that
        // cannot be built, or whose inspection fails, is reported as a warning with its reason.
        private protected override FBXUVMessage Diagnose()
        {
            var layer = Layer;
            FBXUVMakeupMapping mapping;
            try { mapping = layer.GetMapping(); }
            catch (System.Exception exception) { return FBXUVMessage.Warning("検査できません: " + exception.Message); }
            if (mapping == null) return FBXUVMessage.Warning("検査できません: 対応点から変換を作れません。");
            try
            {
                var maximumError = 0f;
                foreach (var point in layer.landmarks)
                    maximumError = Mathf.Max(maximumError, Vector2.Distance(mapping.Evaluate(point.targetUv), point.sourceUv));
                return FBXUVMessage.Info("三角形補間: " + mapping.Exact.TriangleCount + "三角形。局所反転・重なりなし。最終表示は別途確認してください。"
                    + "\n" + mapping.BoundaryReason
                    + "\n対応点の最大ずれ: " + maximumError.ToString("G6") + " 転送元UV");
            }
            catch (System.Exception exception) { return FBXUVMessage.Warning("検査できません: " + exception.Message); }
        }
    }
}
