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
            TryPopulateTargetModelOrPrefab();
        }

        public override void OnInspectorGUI()
        {
            using (InspectorGuiMarker.Auto())
            {
                serializedObject.UpdateIfRequiredOrScript();
                DrawModelPrefabReference(
                    serializedObject.FindProperty("sourceModelOrPrefab"),
                    new GUIContent("転送元 Model / Prefab"));
                var targetModelOrPrefab = serializedObject.FindProperty("targetModelOrPrefab");
                DrawModelPrefabReference(
                    targetModelOrPrefab,
                    new GUIContent("転送先 Model / Prefab"));
                if (targetModelOrPrefab.objectReferenceValue != null)
                {
                    targetAutoResolutionError = string.Empty;
                }
                if (!string.IsNullOrEmpty(targetAutoResolutionError))
                {
                    EditorGUILayout.HelpBox(
                        $"転送先を自動決定できません: {targetAutoResolutionError}",
                        MessageType.Warning);
                }
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

                DrawCanvasTarget((FBXUVTextureTransferLayer)target);
                DrawResourceState();
                DrawEnvironmentNotice();
                DrawValidation((FBXUVTextureTransferLayer)target);

                FBXUVRegionBindingSerializedPropertyUtility.ApplyModifiedProperties(serializedObject);
            }
        }

        private static void DrawModelPrefabReference(
            SerializedProperty property,
            GUIContent label)
        {
            EditorGUI.BeginChangeCheck();
            var selected = EditorGUILayout.ObjectField(
                label,
                property.objectReferenceValue,
                typeof(GameObject),
                false) as GameObject;
            if (EditorGUI.EndChangeCheck()) property.objectReferenceValue = selected;

            string error;
            var root = property.objectReferenceValue as GameObject;
            if (!FBXUVModelPrefabReferenceUtility.TryResolveRoot(root, out _, out error))
            {
                EditorGUILayout.HelpBox(error, MessageType.Error);
            }
        }

        private void TryPopulateTargetModelOrPrefab()
        {
            var transferLayer = target as FBXUVTextureTransferLayer;
            if (FBXUVModelPrefabReferenceUtility.TryPopulateTargetRoot(
                    transferLayer,
                    out _,
                    out var error))
            {
                targetAutoResolutionError = string.Empty;
                return;
            }

            targetAutoResolutionError = error;
        }

        internal static int OrientationPopupIndex(int serializedValue)
        {
            var index = Array.IndexOf(OrientationValues, (FBXUVTransferOrientation)serializedValue);
            return index >= 0 ? index : 0;
        }

        private void DrawCanvasTarget(FBXUVTextureTransferLayer transferLayer)
        {
            EditorGUILayout.Space(6f);
            if (!FBXUVCanvasHierarchyUtility.TryFindCanvas(transferLayer, out var canvas, out var error, out _))
            {
                EditorGUILayout.HelpBox(error, MessageType.Error);
                return;
            }

            using (new EditorGUI.DisabledScope(true))
            {
                EditorGUILayout.ObjectField(
                    "Canvasの対象テクスチャ",
                    canvas.TargetTexture?.SelectTexture,
                    typeof(Texture2D),
                    false);
            }
            EditorGUILayout.HelpBox(
                "適用先は親MultiLayerImageCanvasのTargetTextureだけです。targetごとにMLICとLayerを1対1で配置してください。",
                MessageType.Info);
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
