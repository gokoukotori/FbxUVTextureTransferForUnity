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
        private void OnEnable() => FBXUVModelPrefabReferenceUtility.TryPopulateTargetRoot((FBXUVEyeTextureTransferLayer)target, out _, out _);
        public override void OnInspectorGUI()
        {
            serializedObject.UpdateIfRequiredOrScript();
            EditorGUILayout.LabelField("アイテクスチャ転送（β版）", EditorStyles.boldLabel);
            DrawConfiguration(serializedObject);
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

        internal static void DrawConfiguration(SerializedObject serialized)
        {
            Model(serialized, "sourceModelOrPrefab", "転送元 Model / Prefab");
            Model(serialized, "targetModelOrPrefab", "転送先 Model / Prefab");
            EditorGUILayout.PropertyField(serialized.FindProperty("sourceTexture"), new GUIContent("転送元テクスチャ", "虹彩と瞳孔に使用します。"));
            var layer = serialized.targetObject as FBXUVEyeTextureTransferLayer;
            if (FBXUVCanvasHierarchyUtility.TryFindCanvas(layer, out var canvas, out var error, out _))
            {
                using (new EditorGUI.DisabledScope(true))
                    EditorGUILayout.ObjectField("Canvasの対象テクスチャ", canvas.TargetTexture?.SelectTexture, typeof(Texture), false);
            }
            else EditorGUILayout.HelpBox(error, MessageType.Warning);
        }

        private static void Model(SerializedObject serialized, string property, string label)
        {
            var value = serialized.FindProperty(property);
            value.objectReferenceValue = EditorGUILayout.ObjectField(label, value.objectReferenceValue, typeof(GameObject), false);
            if (value.objectReferenceValue != null && !FBXUVModelPrefabReferenceUtility.TryResolveRoot(value.objectReferenceValue as GameObject, out _, out var error))
                EditorGUILayout.HelpBox(error, MessageType.Error);
        }
    }
}
