using GokouKotori.FBXUVTextureTransfer.Editor.NDMF;
using UnityEditor;
using UnityEngine;

namespace GokouKotori.FBXUVTextureTransfer.Editor
{
    [CustomEditor(typeof(FBXUVEyeTextureTransferLayer))]
    public sealed class FBXUVEyeTextureTransferLayerEditor : FBXUVLayerEditor<FBXUVEyeTextureTransferLayer>
    {
        private string message;
        private protected override void ClearResults() => message = null;
        public override void OnInspectorGUI()
        {
            serializedObject.UpdateIfRequiredOrScript();
            EditorGUILayout.LabelField("アイテクスチャ転送（β版）", EditorStyles.boldLabel);
            DrawModelFields();
            EditorGUILayout.PropertyField(serializedObject.FindProperty("sourceTexture"), new GUIContent("転送元テクスチャ", "虹彩と瞳孔に使用します。"));
            FBXUVLayerInspectorGUI.DrawCanvasTarget(Layer);
            if (serializedObject.ApplyModifiedProperties()) InvalidateResults();
            var layer = Layer;
            DropStaleResults();
            EditorGUILayout.HelpBox("転送先の目のUVへアイテクスチャを転送します。専用エディターで目ごとに「従来型／トポロジー型」と「別パーツあり／画像一体型」を選び、アイランドを設定してください。", MessageType.Info);
            EditorGUILayout.LabelField("登録した目", (layer.eyes?.Count ?? 0).ToString());
            if (GUILayout.Button("アイ転送エディターを開く")) FBXUVEyeTextureTransferWindow.Open(layer);
            if (GUILayout.Button("設定を検証"))
            {
                var issues = FBXUVEyeTransferValidation.ValidateLayer(layer);
                message = issues.Count == 0 ? "設定は有効です。転写結果はTTTプレビューで確認してください。" : string.Join("\n", System.Linq.Enumerable.Select(issues, i => i.Message));
                RememberResultInputs();
            }
            if (!string.IsNullOrEmpty(message)) EditorGUILayout.HelpBox(message, MessageType.Info);
        }
    }
}
