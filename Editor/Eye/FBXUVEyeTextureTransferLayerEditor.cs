using System.Collections.Generic;
using GokouKotori.FBXUVTextureTransfer.Editor.NDMF;
using UnityEditor;
using UnityEngine;

namespace GokouKotori.FBXUVTextureTransfer.Editor
{
    [CustomEditor(typeof(FBXUVEyeTextureTransferLayer))]
    public sealed class FBXUVEyeTextureTransferLayerEditor : FBXUVLayerEditor<FBXUVEyeTextureTransferLayer>
    {
        private protected override IReadOnlyList<FBXUVTransferValidationIssue> Validate()
            => FBXUVEyeTransferValidation.ValidateLayer(Layer);

        private protected override bool HasRequiredResources()
            => Layer.ResolveEyeShader() != null && Layer.ResolvePixelProcessShader() != null;

        public override void OnInspectorGUI()
        {
            serializedObject.UpdateIfRequiredOrScript();
            DrawHeader("アイ転送（β版）");
            DrawModelFields();
            EditorGUILayout.PropertyField(serializedObject.FindProperty("sourceTexture"), new GUIContent("転送元テクスチャ", "虹彩と瞳孔に使用します。"));
            FBXUVLayerInspectorGUI.DrawCanvasTarget(Layer);
            if (serializedObject.ApplyModifiedProperties()) InvalidateResults();
            var layer = Layer;
            EditorGUILayout.HelpBox("転送先の目のUVへアイテクスチャを転送します。" + FBXUVEditorNames.EyeTransferEditor + "で目ごとに「従来型／トポロジー型」と「別パーツあり／画像一体型」を選び、UV島を設定してください。", MessageType.Info);
            EditorGUILayout.LabelField("登録した目", (layer.eyes?.Count ?? 0).ToString());
            if (GUILayout.Button(FBXUVEditorNames.OpenButton(FBXUVEditorNames.EyeTransferEditor))) FBXUVEyeTextureTransferWindow.Open(layer);
            DrawValidation();
        }
    }
}
