using System;
using System.Collections.Generic;
using GokouKotori.FBXUVTextureTransfer.Editor.NDMF;
using UnityEditor;
using UnityEngine;

namespace GokouKotori.FBXUVTextureTransfer.Editor
{
    // Shared shell of the transfer layer inspectors. On enable it fills an empty target Model / Prefab from the parent
    // avatar. The results its buttons compute (validation, diagnostics, inspections) are kept only while their inputs are
    // unchanged: undo / redo, project changes and any change of the layer's serialized values drop all of them at once.
    public abstract class FBXUVLayerEditor<TLayer> : UnityEditor.Editor where TLayer : FBXUVTransferLayerBase
    {
        private string targetAutoResolutionError;
        // The layer's serialized values when the cached results were computed; null while there are none.
        private string resultInputs;
        private IReadOnlyList<FBXUVTransferValidationIssue> validationIssues;

        protected TLayer Layer => (TLayer)target;

        protected virtual void OnEnable()
        {
            FBXUVModelPrefabReferenceUtility.TryPopulateTargetRoot(Layer, out _, out targetAutoResolutionError);
            Undo.undoRedoPerformed += InvalidateResults;
            EditorApplication.projectChanged += InvalidateResults;
        }

        protected virtual void OnDisable()
        {
            Undo.undoRedoPerformed -= InvalidateResults;
            EditorApplication.projectChanged -= InvalidateResults;
        }

        // Clears the cached results of the derived editor. Called by InvalidateResults.
        private protected virtual void ClearResults()
        {
        }

        protected void InvalidateResults()
        {
            resultInputs = null;
            validationIssues = null;
            ClearResults();
            Repaint();
        }

        // Call after computing a cached result: it is kept until the layer's serialized values differ from now.
        protected void RememberResultInputs()
        {
            resultInputs = EditorJsonUtility.ToJson(target);
        }

        // Call before drawing a cached result. The layer is serialized only while a result is cached.
        protected void DropStaleResults()
        {
            if (resultInputs != null && resultInputs != EditorJsonUtility.ToJson(target)) InvalidateResults();
        }

        protected void DrawModelFields()
        {
            FBXUVLayerInspectorGUI.DrawModelFields(serializedObject, ref targetAutoResolutionError);
        }

        // The "現在の設定を検証" button and its result: one error box per issue, or that there are none.
        private protected void DrawValidation(Func<IReadOnlyList<FBXUVTransferValidationIssue>> validate)
        {
            EditorGUILayout.Space(4f);
            DropStaleResults();
            if (GUILayout.Button("現在の設定を検証"))
            {
                validationIssues = validate();
                RememberResultInputs();
            }

            if (validationIssues == null) return;
            if (validationIssues.Count == 0)
            {
                EditorGUILayout.HelpBox("検証エラーはありません。", MessageType.Info);
                return;
            }

            foreach (var issue in validationIssues)
            {
                EditorGUILayout.HelpBox(issue.Message, MessageType.Error);
            }
        }
    }
}
