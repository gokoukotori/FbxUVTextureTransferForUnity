using System;
using System.Collections.Generic;
using GokouKotori.FBXUVTextureTransfer.Editor.NDMF;
using UnityEditor;
using UnityEngine;
using Unity.Profiling;

namespace GokouKotori.FBXUVTextureTransfer.Editor
{
    [CustomEditor(typeof(FBXUVTextureTransferLayer))]
    public sealed class FBXUVTextureTransferLayerEditor : FBXUVLayerEditor<FBXUVTextureTransferLayer>
    {
        private static readonly ProfilerMarker InspectorGuiMarker =
            new ProfilerMarker("FBXUVTextureTransfer.LayerInspector.OnGUI");

        internal static readonly string[] OrientationLabels =
        {
            "そのまま",
            "左右反転",
            "上下反転",
            "180度回転",
        };

        internal static readonly FBXUVTransferOrientation[] OrientationValues =
        {
            FBXUVTransferOrientation.Preserve,
            FBXUVTransferOrientation.FlipHorizontal,
            FBXUVTransferOrientation.FlipVertical,
            FBXUVTransferOrientation.Rotate180,
        };

        public override void OnInspectorGUI()
        {
            using (InspectorGuiMarker.Auto())
            {
                serializedObject.UpdateIfRequiredOrScript();
                DrawHeader("テクスチャ転送");
                DrawModelFields();
                EditorGUILayout.PropertyField(
                    serializedObject.FindProperty("defaultSourceTexture"),
                    new GUIContent("転送元テクスチャ"));

                FBXUVLayerInspectorGUI.DrawCanvasTarget(Layer);

                EditorGUILayout.Space(4f);
                // Regions are edited in the texture transfer editor only, like the eyes and landmarks of the other layers.
                var bindings = FBXUVRegionBindingSerializedPropertyUtility.FindBindings(serializedObject);
                EditorGUILayout.LabelField("登録したRegion", (bindings?.arraySize ?? 0).ToString());

                if (GUILayout.Button(FBXUVEditorNames.OpenButton(FBXUVEditorNames.TextureTransferEditor)))
                {
                    FBXUVTextureTransferWindow.Open(Layer);
                }

                DrawValidation();

                FBXUVRegionBindingSerializedPropertyUtility.ApplyModifiedProperties(serializedObject);
            }
        }

        private protected override IReadOnlyList<FBXUVTransferValidationIssue> Validate()
            => FBXUVTextureTransferValidation.ValidateLayer(Layer);

        internal static int OrientationPopupIndex(int serializedValue)
        {
            var index = Array.IndexOf(OrientationValues, (FBXUVTransferOrientation)serializedValue);
            return index >= 0 ? index : 0;
        }

        private protected override bool HasRequiredResources()
            => Layer.ResolveTriangleShader() != null && Layer.ResolvePixelProcessShader() != null;
    }
}
