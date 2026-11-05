using System;
using System.Collections.Generic;
using GokouKotori.FBXUVTextureTransfer.Editor.NDMF;
using UnityEditor;
using UnityEngine;
using Unity.Profiling;
using UnityEngine.Rendering;

namespace GokouKotori.FBXUVTextureTransfer.Editor
{
    [CustomEditor(typeof(FBXUVTextureTransferLayer))]
    public sealed class FBXUVTextureTransferLayerEditor : UnityEditor.Editor
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

        private IReadOnlyList<FBXUVTransferValidationIssue> lastValidationIssues;
        private string targetAutoResolutionError;

        private void OnEnable()
        {
            FBXUVModelPrefabReferenceUtility.TryPopulateTargetRoot(
                (FBXUVTextureTransferLayer)target, out _, out targetAutoResolutionError);
        }

        public override void OnInspectorGUI()
        {
            using (InspectorGuiMarker.Auto())
            {
                serializedObject.UpdateIfRequiredOrScript();
                FBXUVLayerInspectorGUI.DrawModelFields(serializedObject, ref targetAutoResolutionError);
                EditorGUILayout.PropertyField(
                    serializedObject.FindProperty("defaultSourceTexture"),
                    new GUIContent("転送元テクスチャ"));

                EditorGUILayout.Space(4f);
                // Regions are edited in the UV Region Editor only, like the eyes and landmarks of the other layers.
                var bindings = FBXUVRegionBindingSerializedPropertyUtility.FindBindings(serializedObject);
                EditorGUILayout.LabelField("登録したRegion", (bindings?.arraySize ?? 0).ToString());

                if (GUILayout.Button("UV領域エディターを開く"))
                {
                    FBXUVTextureTransferWindow.Open((FBXUVTextureTransferLayer)target);
                }

                FBXUVLayerInspectorGUI.DrawCanvasTarget((FBXUVTextureTransferLayer)target);
                DrawResourceState();
                DrawEnvironmentNotice();
                DrawValidation((FBXUVTextureTransferLayer)target);

                FBXUVRegionBindingSerializedPropertyUtility.ApplyModifiedProperties(serializedObject);
            }
        }

        internal static int OrientationPopupIndex(int serializedValue)
        {
            var index = Array.IndexOf(OrientationValues, (FBXUVTransferOrientation)serializedValue);
            return index >= 0 ? index : 0;
        }

        private void DrawResourceState()
        {
            var transferLayer = (FBXUVTextureTransferLayer)target;
            if (transferLayer.ResolveTriangleShader() == null || transferLayer.ResolvePixelProcessShader() == null)
            {
                EditorGUILayout.HelpBox(
                    "内部Shader/ComputeShaderを読み込めません。パッケージを再インポートしてください。",
                    MessageType.Error);
            }
        }

        private static void DrawEnvironmentNotice()
        {
            if (Application.platform != RuntimePlatform.WindowsEditor ||
                SystemInfo.graphicsDeviceType != GraphicsDeviceType.Direct3D11)
            {
                EditorGUILayout.HelpBox(
                    "動作要件はUnity 2022.3.22f1以上・Windows・D3D11です。現在のgraphics APIは未検証です。",
                    MessageType.Warning);
            }
        }

        private void DrawValidation(FBXUVTextureTransferLayer transferLayer)
        {
            EditorGUILayout.Space(4f);
            if (GUILayout.Button("現在の設定を検証"))
            {
                lastValidationIssues = FBXUVTextureTransferValidation.ValidateLayer(transferLayer);
            }

            if (lastValidationIssues == null) return;
            if (lastValidationIssues.Count == 0)
            {
                EditorGUILayout.HelpBox("検証エラーはありません。", MessageType.Info);
                return;
            }

            foreach (var issue in lastValidationIssues)
            {
                EditorGUILayout.HelpBox(issue.Message, MessageType.Error);
            }
        }

    }
}
