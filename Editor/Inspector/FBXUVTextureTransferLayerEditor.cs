using System;
using System.Collections.Generic;
using GokouKotori.FBXUVTextureTransfer.Editor.NDMF;
using UnityEditor;
using UnityEditorInternal;
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

        private ReorderableList bindingList;
        private IReadOnlyList<FBXUVTransferValidationIssue> lastValidationIssues;
        private readonly Dictionary<int, string> bindingNameErrors = new Dictionary<int, string>();
        private string targetAutoResolutionError;

        private void OnEnable()
        {
            TryPopulateTargetModelOrPrefab();
            var bindings = FBXUVRegionBindingSerializedPropertyUtility.FindBindings(serializedObject);
            bindingList = new ReorderableList(serializedObject, bindings, true, true, true, true)
            {
                drawHeaderCallback = rect => EditorGUI.LabelField(rect, "Region Binding（上から合成順）"),
                elementHeightCallback = BindingElementHeight,
                drawElementCallback = DrawBinding,
                onAddCallback = AddBinding,
                onRemoveCallback = RemoveBinding,
                onCanRemoveCallback = list => list.count > 0,
                onReorderCallback = _ => bindingNameErrors.Clear()
            };
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
                bindingList?.DoLayoutList();
                if (bindingList == null || bindingList.serializedProperty.arraySize == 0)
                {
                    EditorGUILayout.HelpBox(
                        "Regionがありません。このコンポーネントの＋ボタンから追加してください。",
                        MessageType.Warning);
                }

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

        private void DrawBinding(Rect rect, int index, bool isActive, bool isFocused)
        {
            var binding = bindingList.serializedProperty.GetArrayElementAtIndex(index);
            var enabled = binding.FindPropertyRelative("enabled");
            var name = binding.FindPropertyRelative("name");
            var orientation = binding.FindPropertyRelative("orientation");
            var line = EditorGUIUtility.singleLineHeight;
            var toggleRect = new Rect(rect.x, rect.y + 2f, 18f, line);
            var nameRect = new Rect(rect.x + 22f, rect.y + 2f, rect.width - 22f, line);
            var orientationRect = new Rect(rect.x + 22f, rect.y + line + 6f, rect.width - 22f, line);

            enabled.boolValue = EditorGUI.Toggle(toggleRect, enabled.boolValue);
            var requestedName = EditorGUI.DelayedTextField(nameRect, name.stringValue);
            if (!string.Equals(requestedName, name.stringValue, StringComparison.Ordinal))
            {
                if (FBXUVRegionBindingSerializedPropertyUtility.TryRename(
                        bindingList.serializedProperty,
                        index,
                        requestedName,
                        out var error))
                {
                    bindingNameErrors.Remove(index);
                }
                else
                {
                    bindingNameErrors[index] = error;
                }
            }
            var orientationPopupRect = EditorGUI.PrefixLabel(
                orientationRect,
                new GUIContent("向き補正", "転送元画像を転送先の領域へ配置する向きを手動で指定します。"));
            var currentIndex = OrientationPopupIndex(orientation.intValue);
            EditorGUI.BeginChangeCheck();
            var selectedIndex = EditorGUI.Popup(
                orientationPopupRect,
                currentIndex,
                OrientationLabels);
            if (EditorGUI.EndChangeCheck()) orientation.intValue = (int)OrientationValues[selectedIndex];

            if (bindingNameErrors.TryGetValue(index, out var nameError))
            {
                var helpBoxRect = new Rect(
                    rect.x + 22f,
                    rect.y + line * 2f + 10f,
                    rect.width - 22f,
                    EditorGUIUtility.singleLineHeight * 2f);
                EditorGUI.HelpBox(helpBoxRect, nameError, MessageType.Error);
            }
        }

        private float BindingElementHeight(int index)
        {
            var height = EditorGUIUtility.singleLineHeight * 2f + 8f;
            if (bindingNameErrors.ContainsKey(index))
            {
                height += EditorGUIUtility.singleLineHeight * 2f + 4f;
            }

            return height;
        }

        private void AddBinding(ReorderableList list)
        {
            var index = FBXUVRegionBindingSerializedPropertyUtility.Add(list.serializedProperty);
            if (index < 0) return;

            bindingNameErrors.Clear();
            list.index = index;
        }

        private void RemoveBinding(ReorderableList list)
        {
            var index = list.index;
            if (index < 0 || index >= list.serializedProperty.arraySize) return;

            var binding = list.serializedProperty.GetArrayElementAtIndex(index);
            var regionName = binding.FindPropertyRelative("name").stringValue;
            if (!EditorUtility.DisplayDialog(
                    "Regionを削除",
                    $"「{regionName}」を削除しますか？\n転送元／転送先のUV島の選択も削除されます。",
                    "削除",
                    "キャンセル"))
            {
                return;
            }

            if (!FBXUVRegionBindingSerializedPropertyUtility.DeleteAt(list.serializedProperty, index)) return;

            bindingNameErrors.Clear();
            list.index = Mathf.Min(index, list.serializedProperty.arraySize - 1);
        }

        internal static string OrientationLabel(FBXUVTransferOrientation orientation)
        {
            var index = Array.IndexOf(OrientationValues, orientation);
            return index >= 0
                ? OrientationLabels[index]
                : "不正な値";
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
