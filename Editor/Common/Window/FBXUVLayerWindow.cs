using System;
using UnityEditor;
using UnityEngine;

namespace GokouKotori.FBXUVTextureTransfer.Editor
{
    // Shared shell for the layer editor windows: layer field, Hierarchy follow, editor events,
    // refresh requests, reference change detection and per-window caches.
    public abstract class FBXUVLayerWindow<TLayer> : EditorWindow where TLayer : FBXUVTransferLayerBase
    {
        [SerializeField] private TLayer layer;
        [SerializeField] private Vector2 scrollPosition;
        [NonSerialized] private GameObject cachedSourceModelOrPrefab;
        [NonSerialized] private GameObject cachedTargetModelOrPrefab;
        [NonSerialized] private Texture cachedTargetTexture;
        [NonSerialized] private FBXUVRefreshKind pendingRefresh;
        [NonSerialized] private Vector2 windowMousePosition;
        [NonSerialized] private FBXUVRegionPickerPane[] registeredPanes = Array.Empty<FBXUVRegionPickerPane>();

        private FBXUVMeshAnalysisCache analysisCache = new FBXUVMeshAnalysisCache();
        private readonly FBXUVMeshCandidateCache candidateCache = new FBXUVMeshCandidateCache();

        internal TLayer Layer => layer;
        internal FBXUVMeshAnalysisCache AnalysisCache => analysisCache;
        internal FBXUVMeshCandidateCache CandidateCache => candidateCache;
        internal FBXUVRefreshKind PendingRefresh => pendingRefresh;

        protected GameObject SourceModelOrPrefab => layer == null ? null : layer.SourceModelOrPrefab;
        protected GameObject TargetModelOrPrefab => layer == null ? null : layer.TargetModelOrPrefab;
        protected abstract string MissingLayerMessage { get; }

        protected abstract void DrawContent();

        // Internal because FBXUVRefreshKind is internal. Called with a null Layer after the layer is cleared.
        internal abstract void OnRefresh(FBXUVRefreshKind kind);

        protected virtual void OnLayerChanged()
        {
        }

        // Polled by every ProcessRefresh (OnGUI and OnInspectorUpdate). True when the saved values
        // were changed outside this window (e.g. in the Inspector); requests PreservePending.
        protected virtual bool DetectSavedValueChanges()
        {
            return false;
        }

        protected virtual void OnEnable()
        {
            EditorApplication.projectChanged -= HandleProjectChanged;
            EditorApplication.projectChanged += HandleProjectChanged;
            EditorApplication.hierarchyChanged -= HandleHierarchyChanged;
            EditorApplication.hierarchyChanged += HandleHierarchyChanged;
            Undo.undoRedoPerformed -= HandleUndoRedo;
            Undo.undoRedoPerformed += HandleUndoRedo;
            // Non-serialized pane state is gone after a domain reload; reload the saved values.
            RequestRefresh(FBXUVRefreshKind.RestoreSaved);
        }

        protected virtual void OnDisable()
        {
            EditorApplication.projectChanged -= HandleProjectChanged;
            EditorApplication.hierarchyChanged -= HandleHierarchyChanged;
            Undo.undoRedoPerformed -= HandleUndoRedo;
        }

        protected void OnSelectionChange() => HandleSelectionChange();

        protected void OnInspectorUpdate()
        {
            if (ProcessRefresh()) Repaint();
        }

        protected void OnGUI()
        {
            windowMousePosition = Event.current.mousePosition;
            DrawLayerSelection();
            if (layer == null)
            {
                EditorGUILayout.HelpBox(MissingLayerMessage, MessageType.Info);
                return;
            }

            ProcessRefresh();
            scrollPosition = EditorGUILayout.BeginScrollView(scrollPosition);
            DrawContent();
            EditorGUILayout.EndScrollView();
        }

        internal void SetLayer(TLayer next)
        {
            layer = next;
            // Empty the panes before the new analysis cache is handed over so the old layer's meshes are not re-analysed.
            foreach (var pane in registeredPanes)
            {
                pane.Sync(FBXUVSavedRegion.Empty, FBXUVRefreshKind.RestoreSaved);
            }

            InvalidateAllCaches();
            RequestRefresh(FBXUVRefreshKind.RestoreSaved);
            OnLayerChanged();
            ProcessRefresh();
            Repaint();
        }

        internal void RequestRefresh(FBXUVRefreshKind kind)
        {
            if (kind > pendingRefresh) pendingRefresh = kind;
        }

        internal bool ProcessRefresh()
        {
            var referencesChanged = SynchronizeReferences();
            if (DetectSavedValueChanges()) RequestRefresh(FBXUVRefreshKind.PreservePending);
            var kind = pendingRefresh;
            pendingRefresh = FBXUVRefreshKind.None;
            if (kind != FBXUVRefreshKind.None) OnRefresh(kind);
            return referencesChanged || kind != FBXUVRefreshKind.None;
        }

        internal void HandleUndoRedo()
        {
            InvalidateAllCaches();
            RequestRefresh(FBXUVRefreshKind.RestoreSaved);
            Repaint();
        }

        internal void HandleProjectChanged()
        {
            InvalidateAllCaches();
            RequestRefresh(FBXUVRefreshKind.PreservePending);
            Repaint();
        }

        internal void HandleHierarchyChanged()
        {
            candidateCache.Invalidate();
            Repaint();
        }

        internal void HandleSelectionChange()
        {
            var selected = Selection.activeGameObject;
            if (selected == null) return;
            var selectedLayer = selected.GetComponent<TLayer>();
            if (selectedLayer == null || selectedLayer == layer) return;
            SetLayer(selectedLayer);
        }

        internal static float PreviewSize(float windowWidth, int paneCount)
        {
            var paneWidth = (windowWidth - 24f - 16f * (paneCount - 1)) / paneCount;
            return Mathf.Clamp(paneWidth - 16f, 260f, 640f);
        }

        // Internal because FBXUVRegionPickerPane is internal. Call once per window; the base then hands every new
        // analysis cache to these panes and empties them on layer changes.
        internal void RegisterPanes(params FBXUVRegionPickerPane[] panes)
        {
            registeredPanes = panes ?? Array.Empty<FBXUVRegionPickerPane>();
            foreach (var pane in registeredPanes)
            {
                pane.SetAnalysisCache(analysisCache);
            }
        }

        // Internal because FBXUVPaneContext is internal. Windows set only their own extras on the result.
        internal FBXUVPaneContext CreatePaneContext(
            FBXUVMeshCandidateCache.Snapshot candidates,
            string candidateError,
            bool isTarget,
            Func<string> replacementWarning,
            Texture background,
            float size)
        {
            return new FBXUVPaneContext
            {
                Candidates = candidates,
                CandidateError = candidateError,
                ReplacementWarning = replacementWarning,
                Background = background,
                Size = size,
                InputEnabled = !isTarget || FBXUVCanvasHierarchyUtility.TryFindCanvas(layer, out _, out _, out _),
                WindowMousePosition = windowMousePosition,
                Notify = Notify,
                Repaint = Repaint
            };
        }

        protected void RecordChange(string undoLabel, Action change)
        {
            if (layer == null) return;
            Undo.RecordObject(layer, undoLabel);
            change();
            EditorUtility.SetDirty(layer);
            PrefabUtility.RecordPrefabInstancePropertyModifications(layer);
            Repaint();
        }

        protected Texture ResolveTargetTexture()
        {
            return FBXUVCanvasHierarchyUtility.ResolveTargetTexture(layer);
        }

        protected void Notify(string message)
        {
            ShowNotification(new GUIContent(message));
        }

        private void DrawLayerSelection()
        {
            EditorGUI.BeginChangeCheck();
            var next = (TLayer)EditorGUILayout.ObjectField("編集中", layer, typeof(TLayer), true);
            if (!EditorGUI.EndChangeCheck()) return;

            SetLayer(next);
            // The rest of this GUI pass was laid out for the previous layer.
            GUIUtility.ExitGUI();
        }

        // Model / Prefab or Canvas texture changes only invalidate candidates (spec §4).
        private bool SynchronizeReferences()
        {
            var sourceRoot = SourceModelOrPrefab;
            var targetRoot = TargetModelOrPrefab;
            var targetTexture = ResolveTargetTexture();
            var changed = cachedSourceModelOrPrefab != sourceRoot ||
                          cachedTargetModelOrPrefab != targetRoot ||
                          cachedTargetTexture != targetTexture;
            if (!changed) return false;

            cachedSourceModelOrPrefab = sourceRoot;
            cachedTargetModelOrPrefab = targetRoot;
            cachedTargetTexture = targetTexture;
            candidateCache.Invalidate();
            RequestRefresh(FBXUVRefreshKind.PreservePending);
            return true;
        }

        // Every caller also requests a refresh, so the panes reload their islands from the new cache in OnRefresh.
        private void InvalidateAllCaches()
        {
            analysisCache = new FBXUVMeshAnalysisCache();
            candidateCache.Invalidate();
            foreach (var pane in registeredPanes)
            {
                pane.SetAnalysisCache(analysisCache);
            }
        }
    }
}
