using System.Collections.Generic;
using GokouKotori.FBXUVTextureTransfer.Editor.NDMF;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

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
        private FBXUVMessage? diagnostics;

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
            diagnostics = null;
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

        internal const string EnvironmentWarningMessage = "動作要件は Unity 2022.3.22f1 以上・Windows・D3D11 です。現在のグラフィックス API は未検証です。";
        internal const string MissingResourcesMessage = "内部 Shader / ComputeShader を読み込めません。パッケージを再インポートしてください。";

        // The warning for an unverified environment, or null on Windows with D3D11.
        internal static string EnvironmentWarning(RuntimePlatform platform, GraphicsDeviceType device)
            => platform == RuntimePlatform.WindowsEditor && device == GraphicsDeviceType.Direct3D11
                ? null
                : EnvironmentWarningMessage;

        // True when every internal Shader / ComputeShader the layer needs can be loaded.
        private protected abstract bool HasRequiredResources();

        internal bool RequiredResourcesAvailable => HasRequiredResources();

        // The inspector heading, then the environment warning and the missing internal resources error.
        private protected void DrawHeader(string title)
        {
            EditorGUILayout.LabelField(title, EditorStyles.boldLabel);
            var environmentWarning = EnvironmentWarning(Application.platform, SystemInfo.graphicsDeviceType);
            if (environmentWarning != null) EditorGUILayout.HelpBox(environmentWarning, MessageType.Warning);
            if (!HasRequiredResources()) EditorGUILayout.HelpBox(MissingResourcesMessage, MessageType.Error);
        }

        internal const string NoIssuesMessage = "検証エラーはありません。仕上がりは TexTransTool / NDMF のプレビューで確認してください。";

        // Validates the layer's current settings. The result is shown by DrawValidation.
        private protected abstract IReadOnlyList<FBXUVTransferValidationIssue> Validate();

        // The result of the last validation; null while there is none (or it was dropped as stale).
        internal IReadOnlyList<FBXUVTransferValidationIssue> ValidationIssues => validationIssues;

        // Runs the validation and keeps its result until the layer's inputs change.
        internal void RunValidation()
        {
            validationIssues = Validate();
            RememberResultInputs();
        }

        // Inspects the layer's current settings for the "診断" button. The result is shown by DrawDiagnostics.
        private protected virtual FBXUVMessage Diagnose() => default;

        // The result of the last diagnosis; null while there is none (or it was dropped as stale).
        internal FBXUVMessage? Diagnostics => diagnostics;

        // Runs the diagnosis and keeps its result until the layer's inputs change.
        internal void RunDiagnostics()
        {
            diagnostics = Diagnose();
            RememberResultInputs();
        }

        // The "診断" button and its result.
        private protected void DrawDiagnostics()
        {
            EditorGUILayout.Space(4f);
            DropStaleResults();
            if (GUILayout.Button("診断")) RunDiagnostics();
            diagnostics?.Draw();
        }

        // The "現在の設定を検証" button and its result: one error box per issue, or that there are none.
        private protected void DrawValidation()
        {
            EditorGUILayout.Space(4f);
            DropStaleResults();
            if (GUILayout.Button("現在の設定を検証")) RunValidation();

            if (validationIssues == null) return;
            if (validationIssues.Count == 0)
            {
                EditorGUILayout.HelpBox(NoIssuesMessage, MessageType.Info);
                return;
            }

            foreach (var issue in validationIssues)
            {
                EditorGUILayout.HelpBox(issue.Message, MessageType.Error);
            }
        }
    }
}
