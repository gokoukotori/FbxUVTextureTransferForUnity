using GokouKotori.FBXUVTextureTransfer.Editor.NDMF;
using UnityEditor;
using UnityEngine;

namespace GokouKotori.FBXUVTextureTransfer.Editor
{
    [CustomEditor(typeof(FBXUVEyeTextureTransferLayer))]
    public sealed class FBXUVEyeTextureTransferLayerEditor : UnityEditor.Editor
    {
        private string message;
        private string inspectionState;
        private string targetAutoResolutionError;
        private void OnEnable() => FBXUVModelPrefabReferenceUtility.TryPopulateTargetRoot((FBXUVEyeTextureTransferLayer)target, out _, out targetAutoResolutionError);
        public override void OnInspectorGUI()
        {
            serializedObject.UpdateIfRequiredOrScript();
            EditorGUILayout.LabelField("アイテクスチャ転送（β版）", EditorStyles.boldLabel);
            FBXUVLayerInspectorGUI.DrawModelFields(serializedObject, ref targetAutoResolutionError);
            EditorGUILayout.PropertyField(serializedObject.FindProperty("sourceTexture"), new GUIContent("転送元テクスチャ", "虹彩と瞳孔に使用します。"));
            FBXUVLayerInspectorGUI.DrawCanvasTarget((FBXUVEyeTextureTransferLayer)target);
            if (serializedObject.ApplyModifiedProperties()) message = null;
            var layer = (FBXUVEyeTextureTransferLayer)target;
            if (inspectionState != EditorJsonUtility.ToJson(layer)) message = null;
            EditorGUILayout.HelpBox("転送先の目のUVへアイテクスチャを転送します。専用エディターで目ごとに「従来型／トポロジー型」と「別パーツあり／画像一体型」を選び、アイランドを設定してください。", MessageType.Info);
            EditorGUILayout.LabelField("登録した目", (layer.eyes?.Count ?? 0).ToString());
            if (GUILayout.Button("アイ転送エディターを開く")) FBXUVEyeTextureTransferWindow.Open(layer);
            if (GUILayout.Button("設定を検証"))
            {
                var issues = FBXUVEyeTransferValidation.ValidateLayer(layer);
                message = issues.Count == 0 ? "設定は有効です。転写結果はTTTプレビューで確認してください。" : string.Join("\n", System.Linq.Enumerable.Select(issues, i => i.Message));
                inspectionState = EditorJsonUtility.ToJson(layer);
            }
            if (!string.IsNullOrEmpty(message)) EditorGUILayout.HelpBox(message, MessageType.Info);
        }
    }
}
