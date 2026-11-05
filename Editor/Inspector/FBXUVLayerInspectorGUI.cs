using UnityEditor;
using UnityEngine;

namespace GokouKotori.FBXUVTextureTransfer.Editor
{
    // Inspector parts shared by the transfer layers: the Model / Prefab fields and the parent canvas texture.
    internal static class FBXUVLayerInspectorGUI
    {
        private static readonly GUIContent SourceModelLabel = new GUIContent("転送元 Model / Prefab");
        private static readonly GUIContent TargetModelLabel = new GUIContent("転送先 Model / Prefab");

        // targetAutoResolutionError is the reason the target could not be filled from the parent avatar (OnEnable);
        // it is cleared once a target is set.
        internal static void DrawModelFields(SerializedObject serializedLayer, ref string targetAutoResolutionError)
        {
            DrawModelField(serializedLayer.FindProperty("sourceModelOrPrefab"), SourceModelLabel);
            var target = serializedLayer.FindProperty("targetModelOrPrefab");
            DrawModelField(target, TargetModelLabel);
            if (target.objectReferenceValue != null) targetAutoResolutionError = null;
            if (!string.IsNullOrEmpty(targetAutoResolutionError))
            {
                EditorGUILayout.HelpBox($"転送先を自動決定できません: {targetAutoResolutionError}", MessageType.Warning);
            }
        }

        internal static void DrawCanvasTarget(Component layer)
        {
            EditorGUILayout.Space(6f);
            if (!FBXUVCanvasHierarchyUtility.TryFindCanvas(layer, out var canvas, out var error, out _))
            {
                EditorGUILayout.HelpBox(error, MessageType.Error);
                return;
            }

            using (new EditorGUI.DisabledScope(true))
            {
                EditorGUILayout.ObjectField("Canvasの対象テクスチャ", canvas.TargetTexture?.SelectTexture, typeof(Texture), false);
            }

            EditorGUILayout.HelpBox(
                "適用先は親MultiLayerImageCanvasのTargetTextureだけです。targetごとにMLICとLayerを1対1で配置してください。",
                MessageType.Info);
        }

        private static void DrawModelField(SerializedProperty property, GUIContent label)
        {
            EditorGUI.BeginChangeCheck();
            var selected = EditorGUILayout.ObjectField(label, property.objectReferenceValue, typeof(GameObject), false);
            if (EditorGUI.EndChangeCheck()) property.objectReferenceValue = selected;

            if (!FBXUVModelPrefabReferenceUtility.TryResolveRoot(property.objectReferenceValue as GameObject, out _, out var error))
            {
                EditorGUILayout.HelpBox(error, MessageType.Error);
            }
        }
    }
}
