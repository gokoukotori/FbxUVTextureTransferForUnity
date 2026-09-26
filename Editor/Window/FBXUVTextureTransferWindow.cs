using System;
using System.Collections.Generic;
using GokouKotori.FBXUVTextureTransfer;
using UnityEditor;
using UnityEngine;
using Unity.Profiling;

namespace GokouKotori.FBXUVTextureTransfer.Editor
{
    public sealed class FBXUVTextureTransferWindow : EditorWindow
    {
        private static readonly ProfilerMarker WindowGuiMarker =
            new ProfilerMarker("FBXUVTextureTransfer.EditorWindow.OnGUI");
        private static readonly ProfilerMarker MeshAnalysisMarker =
            new ProfilerMarker("FBXUVTextureTransfer.EditorWindow.MeshAnalysis");

        private enum Side
        {
            Source,
            Target
        }

        private enum RegionReconcileRequest
        {
            None,
            PreservePendingSelections,
            ReplacePendingSelections
        }

        [Serializable]
        private sealed class ViewState
        {
            public Mesh mesh;
            public int subMeshIndex;
            public int uvChannel;
            public Texture2D backgroundTexture;
            public int selectedIslandId = -1;

            [NonSerialized] public List<FBXUVIsland> islands;
            [NonSerialized] public Mesh cachedMesh;
            [NonSerialized] public int cachedSubMeshIndex = -1;
            [NonSerialized] public int cachedUvChannel = -1;
            [NonSerialized] public string extractionError;
            [NonSerialized] public bool hasPendingRegionSelection;
        }

        [SerializeField] private FBXUVTextureTransferLayer layer;
        [SerializeField] private ViewState sourceView = new ViewState();
        [SerializeField] private ViewState targetView = new ViewState();
        [SerializeField] private string activeRegionName = string.Empty;
        [SerializeField] private int activeRegionIndex = -1;
        [SerializeField] private Vector2 scrollPosition;
        [NonSerialized] private SerializedObject serializedLayer;
        [NonSerialized] private GameObject cachedSourceModelOrPrefab;
        [NonSerialized] private GameObject cachedTargetModelOrPrefab;
        [NonSerialized] private Texture2D cachedTargetTexture;
        [NonSerialized] private RegionReconcileRequest regionReconcileRequest;

        private FBXUVMeshAnalysisCache meshAnalysisCache = new FBXUVMeshAnalysisCache();
        private readonly Vector3[] triangleLinePoints = new Vector3[4];
        private readonly Vector3[] frameLinePoints = new Vector3[5];

        private readonly FBXUVMeshCandidateCache candidateCache = new FBXUVMeshCandidateCache();
        private bool regionNameLabelsValid;
        private string[] regionNameLabels;

        internal int MeshAnalysisCacheCount => meshAnalysisCache.AnalysisCount;
        internal SerializedObject CachedSerializedLayer => serializedLayer;

        [MenuItem("Tools/Gokoukotori/FBX UV Texture Transfer/UV Region Editor")]
        public static void Open()
        {
            GetWindow<FBXUVTextureTransferWindow>("FBX UV Texture Transfer");
        }

        public static void Open(FBXUVTextureTransferLayer targetLayer)
        {
            var window = GetWindow<FBXUVTextureTransferWindow>("FBX UV Texture Transfer");
            window.layer = targetLayer;
            window.InitializeFromLayer();
            window.Show();
            window.Focus();
        }

        private void OnEnable()
        {
            minSize = new Vector2(760f, 620f);
            sourceView ??= new ViewState();
            targetView ??= new ViewState();
            EditorApplication.projectChanged -= OnProjectChanged;
            EditorApplication.projectChanged += OnProjectChanged;
            EditorApplication.hierarchyChanged -= OnHierarchyChanged;
            EditorApplication.hierarchyChanged += OnHierarchyChanged;
            Undo.undoRedoPerformed -= OnUndoRedoPerformed;
            Undo.undoRedoPerformed += OnUndoRedoPerformed;
            InitializeFromLayer();
        }

        private void OnDisable()
        {
            EditorApplication.projectChanged -= OnProjectChanged;
            EditorApplication.hierarchyChanged -= OnHierarchyChanged;
            Undo.undoRedoPerformed -= OnUndoRedoPerformed;
        }

        private void OnProjectChanged()
        {
            InvalidateAllCaches();
            RequestRegionReconcile(RegionReconcileRequest.PreservePendingSelections);
            Repaint();
        }

        private void OnHierarchyChanged()
        {
            InvalidateCandidateCaches();
            Repaint();
        }

        private void OnUndoRedoPerformed()
        {
            InvalidateAllCaches();
            RequestRegionReconcile(RegionReconcileRequest.ReplacePendingSelections);
            Repaint();
        }

        private void OnSelectionChange()
        {
            if (Selection.activeGameObject != null)
            {
                var selectedLayer = Selection.activeGameObject.GetComponent<FBXUVTextureTransferLayer>();
                if (selectedLayer != null)
                {
                    layer = selectedLayer;
                    InitializeFromLayer();
                    Repaint();
                }
            }
        }

        private void OnInspectorUpdate()
        {
            var changed = SynchronizeModelOrPrefabReferences();
            var currentSerializedLayer = GetSerializedLayer();
            if (currentSerializedLayer != null)
            {
                if (currentSerializedLayer.UpdateIfRequiredOrScript())
                {
                    regionNameLabelsValid = false;
                    RequestRegionReconcile(RegionReconcileRequest.PreservePendingSelections);
                }
                var reconcileRequest = ConsumeRegionReconcileRequest();
                changed |= ReconcileActiveRegion(
                    currentSerializedLayer,
                    reconcileRequest != RegionReconcileRequest.None,
                    reconcileRequest == RegionReconcileRequest.PreservePendingSelections);
            }

            if (changed) Repaint();
        }

        private void OnGUI()
        {
            using (WindowGuiMarker.Auto())
            {
                DrawLayerSelection();
                if (layer == null)
                {
                    EditorGUILayout.HelpBox(
                        "FBXUVTextureTransferLayer を指定してください。HierarchyでLayerを選択しても開けます。",
                        MessageType.Info);
                    return;
                }

                var currentSerializedLayer = GetSerializedLayer();
                if (currentSerializedLayer == null) return;
                if (currentSerializedLayer.UpdateIfRequiredOrScript())
                {
                    regionNameLabelsValid = false;
                    RequestRegionReconcile(RegionReconcileRequest.PreservePendingSelections);
                }
                SynchronizeModelOrPrefabReferences();
                var reconcileRequest = ConsumeRegionReconcileRequest();
                ReconcileActiveRegion(
                    currentSerializedLayer,
                    reconcileRequest != RegionReconcileRequest.None,
                    reconcileRequest == RegionReconcileRequest.PreservePendingSelections);

                scrollPosition = EditorGUILayout.BeginScrollView(scrollPosition);
                if (DrawRegionSelection(currentSerializedLayer))
                {
                    DrawViews(currentSerializedLayer);
                }
                EditorGUILayout.EndScrollView();
            }
        }

        private void DrawLayerSelection()
        {
            EditorGUI.BeginChangeCheck();
            var next = (FBXUVTextureTransferLayer)EditorGUILayout.ObjectField(
                "編集中",
                layer,
                typeof(FBXUVTextureTransferLayer),
                true);
            if (EditorGUI.EndChangeCheck())
            {
                layer = next;
                InitializeFromLayer();
            }
        }

        private bool DrawRegionSelection(SerializedObject serializedLayer)
        {
            EditorGUILayout.Space(8f);
            EditorGUILayout.LabelField("UV島の対応付け", EditorStyles.boldLabel);
            var names = CollectRegionNames(serializedLayer);
            if (names.Length == 0)
            {
                EditorGUILayout.HelpBox(
                    "Regionがありません。FBXUVTextureTransferLayerコンポーネントのInspectorでRegionを追加してください。",
                    MessageType.Info);
                return false;
            }

            EditorGUI.BeginChangeCheck();
            var selected = EditorGUILayout.Popup("マッピングするRegion", activeRegionIndex, names);
            if (EditorGUI.EndChangeCheck())
            {
                activeRegionIndex = selected;
                activeRegionName = GetActiveBinding(serializedLayer)?
                    .FindPropertyRelative("name")?.stringValue ?? string.Empty;
                RefreshSelectedIslandIds(serializedLayer);
            }

            return true;
        }

        private void DrawViews(SerializedObject serializedLayer)
        {
            EditorGUILayout.Space(8f);
            var availableWidth = Mathf.Max(680f, position.width - 38f);
            var previewSize = Mathf.Clamp((availableWidth - 16f) * 0.5f, 300f, 640f);
            using (new EditorGUILayout.HorizontalScope())
            {
                DrawView(serializedLayer, Side.Source, sourceView, previewSize);
                DrawView(serializedLayer, Side.Target, targetView, previewSize);
            }
        }

        private void DrawView(SerializedObject serializedLayer, Side side, ViewState state, float width)
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox, GUILayout.Width(width)))
            {
                EditorGUILayout.LabelField(side == Side.Source ? "転送元UV" : "転送先UV", EditorStyles.boldLabel);
                DrawMeshSelection(side, state);
                if (side == Side.Target)
                {
                    EditorGUI.BeginChangeCheck();
                    state.backgroundTexture = (Texture2D)EditorGUILayout.ObjectField(
                        "背景テクスチャ",
                        state.backgroundTexture,
                        typeof(Texture2D),
                        false);
                    if (EditorGUI.EndChangeCheck()) Repaint();
                }

                EnsureIslands(state);
                var previewRect = GUILayoutUtility.GetRect(width - 16f, width - 16f, GUILayout.ExpandWidth(false));
                var backgroundTexture = side == Side.Source
                    ? ObjectReference<Texture2D>(serializedLayer, "defaultSourceTexture")
                    : state.backgroundTexture;
                DrawUvPreview(previewRect, state, backgroundTexture);
                HandleUvClick(previewRect, serializedLayer, side, state);
                if (!string.IsNullOrEmpty(state.extractionError))
                {
                    EditorGUILayout.HelpBox(state.extractionError, MessageType.Error);
                }
                else
                {
                    EditorGUILayout.LabelField(
                        $"UV島: {(state.islands?.Count ?? 0)} / 選択: {(state.selectedIslandId < 0 ? "なし" : state.selectedIslandId.ToString())}");
                }
            }
        }

        private void DrawMeshSelection(Side side, ViewState state)
        {
            if (side == Side.Target)
            {
                DrawTargetMeshSelection(state);
                return;
            }

            DrawSourceMeshSelection(state);
        }

        private void DrawSourceMeshSelection(ViewState state)
        {
            var meshes = CollectSourceMeshes();
            var meshIndex = meshes.FindIndex(item => item.Mesh == state.mesh);
            if (meshIndex >= 0)
            {
                EditorGUI.BeginChangeCheck();
                meshIndex = EditorGUILayout.Popup("メッシュ", meshIndex, candidateCache.Source.Labels);
                if (EditorGUI.EndChangeCheck())
                {
                    ApplyMeshSelection(state, meshes[meshIndex].Mesh, 0);
                }
            }
            else
            {
                using (new EditorGUI.DisabledScope(true))
                {
                    EditorGUILayout.ObjectField("現在のRegionのメッシュ", state.mesh, typeof(Mesh), false);
                    EditorGUILayout.IntField("現在のRegionのサブメッシュ", state.subMeshIndex);
                }

                EditorGUILayout.HelpBox(CreateSourceSelectionWarning(state, meshes.Count), MessageType.Warning);
                if (meshes.Count > 0)
                {
                    var replacementIndex = EditorGUILayout.Popup("転送元メッシュ", 0, candidateCache.Source.ReplacementLabels);
                    if (replacementIndex > 0)
                    {
                        ApplyMeshSelection(state, meshes[replacementIndex - 1].Mesh, 0);
                    }
                }
            }

            using (new EditorGUI.DisabledScope(state.mesh == null))
            {
                var subMeshCount = state.mesh == null ? 1 : Mathf.Max(1, state.mesh.subMeshCount);
                EditorGUI.BeginChangeCheck();
                state.subMeshIndex = EditorGUILayout.IntSlider("サブメッシュ", state.subMeshIndex, 0, subMeshCount - 1);
                state.uvChannel = EditorGUILayout.IntSlider("UVチャンネル", state.uvChannel, 0, 7);
                if (EditorGUI.EndChangeCheck()) MarkRegionSelectionPending(state);
            }
        }

        private void DrawTargetMeshSelection(ViewState state)
        {
            var targetTexture = ResolveTargetTexture();
            var candidates = CollectTargetMeshCandidates(targetTexture);
            TrySelectOnlyTargetCandidate(state, candidates);
            var options = CollectTargetMeshOptions(targetTexture);
            var selectedIndex = options.FindIndex(option =>
                option.Mesh == state.mesh && option.SubMeshIndex == state.subMeshIndex);

            if (selectedIndex >= 0)
            {
                EditorGUI.BeginChangeCheck();
                selectedIndex = EditorGUILayout.Popup(
                    "メッシュ / サブメッシュ",
                    selectedIndex,
                    candidateCache.Target.Labels);
                if (EditorGUI.EndChangeCheck())
                {
                    ApplyMeshSelection(state, options[selectedIndex].Mesh, options[selectedIndex].SubMeshIndex);
                }
            }
            else
            {
                using (new EditorGUI.DisabledScope(true))
                {
                    EditorGUILayout.ObjectField("現在のRegionのメッシュ", state.mesh, typeof(Mesh), false);
                    EditorGUILayout.IntField("現在のRegionのサブメッシュ", state.subMeshIndex);
                }

                EditorGUILayout.HelpBox(
                    CreateTargetSelectionWarning(state, targetTexture, options.Count),
                    MessageType.Warning);

                if (options.Count > 0)
                {
                    var replacementIndex = EditorGUILayout.Popup(
                        "転送先メッシュ / サブメッシュ",
                        0,
                        candidateCache.Target.ReplacementLabels);
                    if (replacementIndex > 0)
                    {
                        var replacement = options[replacementIndex - 1];
                        ApplyMeshSelection(state, replacement.Mesh, replacement.SubMeshIndex);
                    }
                }
            }

            using (new EditorGUI.DisabledScope(state.mesh == null ||
                       !FBXUVCanvasHierarchyUtility.TryFindCanvas(layer, out _, out _, out _)))
            {
                EditorGUI.BeginChangeCheck();
                state.uvChannel = EditorGUILayout.IntSlider("UVチャンネル", state.uvChannel, 0, 7);
                if (EditorGUI.EndChangeCheck()) MarkRegionSelectionPending(state);
            }
        }

        private Texture2D ResolveTargetTexture()
        {
            return FBXUVCanvasHierarchyUtility.TryFindCanvas(layer, out var canvas, out _, out _)
                ? canvas.TargetTexture?.SelectTexture
                : null;
        }

        private string CreateTargetSelectionWarning(ViewState state, Texture2D targetTexture, int optionCount)
        {
            if (!FBXUVCanvasHierarchyUtility.TryFindCanvas(layer, out _, out var error, out _))
            {
                return $"{error} 既存のTarget Region値は保持しています。";
            }

            if (targetTexture == null)
            {
                return "親MultiLayerImageCanvasのTargetTextureを設定してください。既存のTarget Region値は保持しています。";
            }

            var targetRoot = layer == null ? null : layer.TargetModelOrPrefab;
            if (!FBXUVModelPrefabReferenceUtility.TryResolveRoot(targetRoot, out _, out var rootError))
            {
                return $"転送先 Model / Prefab: {rootError} 既存のTarget Region値は保持しています。";
            }

            if (optionCount == 0)
            {
                return "TargetTextureを参照するMaterialを持つRendererが転送先 Model / Prefab配下にありません。既存のTarget Region値は保持しています。";
            }

            return state.mesh == null
                ? "現在のTarget RegionにMesh / Submeshがありません。候補から明示的に選択してください。"
                : "Target RegionのMesh / Submeshは現在のTargetTextureを参照するRendererで使用されていません。既存値は保持しています。置き換える場合だけ候補から明示的に選択してください。";
        }

        private string CreateSourceSelectionWarning(ViewState state, int optionCount)
        {
            var sourceRoot = layer == null ? null : layer.SourceModelOrPrefab;
            if (!FBXUVModelPrefabReferenceUtility.TryResolveRoot(sourceRoot, out _, out var rootError))
            {
                return $"転送元 Model / Prefab: {rootError} 既存のSource Region値は保持しています。";
            }

            if (optionCount == 0)
            {
                return "転送元 Model / Prefab配下にMeshがありません。既存のSource Region値は保持しています。";
            }

            return state.mesh == null
                ? "現在のSource RegionにMeshがありません。候補から明示的に選択してください。"
                : "Source RegionのMeshは現在の転送元 Model / Prefab配下にありません。既存値は保持しています。置き換える場合だけ候補から明示的に選択してください。";
        }

        private static void ApplyMeshSelection(ViewState state, Mesh mesh, int subMeshIndex)
        {
            state.mesh = mesh;
            state.subMeshIndex = subMeshIndex;
            MarkRegionSelectionPending(state);
        }

        private static bool TrySelectOnlyTargetCandidate(
            ViewState state,
            IReadOnlyList<FBXUVTargetMeshCandidate> candidates)
        {
            if (candidates.Count != 1 || candidates[0].SubMeshIndices.Count != 1) return false;

            var candidate = candidates[0];
            var subMeshIndex = candidate.SubMeshIndices[0];
            if (state.mesh == candidate.Mesh && state.subMeshIndex == subMeshIndex) return false;

            ApplyMeshSelection(state, candidate.Mesh, subMeshIndex);
            return true;
        }

        private static void MarkRegionSelectionPending(ViewState state)
        {
            state.hasPendingRegionSelection = true;
            Invalidate(state);
        }

        private void DrawUvPreview(Rect rect, ViewState state, Texture2D backgroundTexture)
        {
            EditorGUI.DrawRect(rect, new Color(0.08f, 0.08f, 0.08f, 1f));
            if (backgroundTexture != null)
            {
                GUI.DrawTexture(rect, backgroundTexture, ScaleMode.StretchToFill, true);
            }

            if (Event.current.type != EventType.Repaint) return;

            Handles.BeginGUI();
            if (state.islands != null)
            {
                foreach (var island in state.islands)
                {
                    var selected = island.Id == state.selectedIslandId;
                    Handles.color = selected ? new Color(1f, 0.72f, 0.05f, 1f) : new Color(0.1f, 1f, 0.75f, 0.85f);
                    foreach (var triangle in island.Triangles)
                    {
                        triangleLinePoints[0] = UvToGui(rect, triangle.A);
                        triangleLinePoints[1] = UvToGui(rect, triangle.B);
                        triangleLinePoints[2] = UvToGui(rect, triangle.C);
                        triangleLinePoints[3] = triangleLinePoints[0];
                        Handles.DrawAAPolyLine(selected ? 2.2f : 1.1f, triangleLinePoints);
                    }
                }
            }

            Handles.color = new Color(1f, 1f, 1f, 0.3f);
            frameLinePoints[0] = new Vector3(rect.x, rect.y);
            frameLinePoints[1] = new Vector3(rect.xMax, rect.y);
            frameLinePoints[2] = new Vector3(rect.xMax, rect.yMax);
            frameLinePoints[3] = new Vector3(rect.x, rect.yMax);
            frameLinePoints[4] = frameLinePoints[0];
            Handles.DrawAAPolyLine(1f, frameLinePoints);
            Handles.EndGUI();
        }

        private void HandleUvClick(Rect rect, SerializedObject serializedLayer, Side side, ViewState state)
        {
            var current = Event.current;
            if (current.type != EventType.MouseDown || current.button != 0 || !rect.Contains(current.mousePosition))
            {
                return;
            }

            if (activeRegionIndex < 0)
            {
                ShowNotification(new GUIContent("コンポーネントでRegionを追加し、マッピング対象を選択してください。"));
                current.Use();
                return;
            }

            EnsureIslands(state);
            if (state.islands == null || state.mesh == null)
            {
                current.Use();
                return;
            }

            if (!IsMeshSelectionAvailable(side, state))
            {
                ShowNotification(new GUIContent("現在のModel / Prefab候補からMeshを選択してください。"));
                current.Use();
                return;
            }

            var uv = new Vector2(
                Mathf.InverseLerp(rect.x, rect.xMax, current.mousePosition.x),
                1f - Mathf.InverseLerp(rect.y, rect.yMax, current.mousePosition.y));
            var island = FBXUVIslandExtractor.HitTest(state.islands, uv);
            if (island == null)
            {
                ShowNotification(new GUIContent("この位置にUV島はありません。"));
                current.Use();
                return;
            }

            var binding = GetActiveBinding(serializedLayer);
            if (binding == null)
            {
                ShowNotification(new GUIContent("選択中のregionがLayerにありません。"));
                current.Use();
                return;
            }

            StoreIsland(serializedLayer, binding, side, state, island);
            state.selectedIslandId = island.Id;
            state.extractionError = null;
            current.Use();
            Repaint();
        }

        private void StoreIsland(
            SerializedObject serializedLayer,
            SerializedProperty binding,
            Side side,
            ViewState state,
            FBXUVIsland island)
        {
            if (side == Side.Target &&
                !FBXUVCanvasHierarchyUtility.TryFindCanvas(layer, out _, out _, out _)) return;

            var region = binding.FindPropertyRelative(side == Side.Source ? "sourceRegion" : "targetRegion");
            if (region == null) return;

            Undo.RecordObject(serializedLayer.targetObject, "UV島を選択");
            region.FindPropertyRelative("mesh").objectReferenceValue = state.mesh;
            region.FindPropertyRelative("subMeshIndex").intValue = state.subMeshIndex;
            region.FindPropertyRelative("uvChannel").intValue = state.uvChannel;
            region.FindPropertyRelative("islandId").intValue = island.Id;
            region.FindPropertyRelative("meshHash").stringValue =
                GetMeshHash(state.mesh, state.subMeshIndex, state.uvChannel);

            var bounds = region.FindPropertyRelative("bounds");
            bounds.FindPropertyRelative("minU").floatValue = island.Bounds.MinU;
            bounds.FindPropertyRelative("minV").floatValue = island.Bounds.MinV;
            bounds.FindPropertyRelative("maxU").floatValue = island.Bounds.MaxU;
            bounds.FindPropertyRelative("maxV").floatValue = island.Bounds.MaxV;

            var triangles = region.FindPropertyRelative("triangles");
            triangles.arraySize = island.Triangles.Count;
            for (var index = 0; index < island.Triangles.Count; index++)
            {
                var source = island.Triangles[index];
                var destination = triangles.GetArrayElementAtIndex(index);
                destination.FindPropertyRelative("index").intValue = source.Index;
                destination.FindPropertyRelative("a").vector2Value = source.A;
                destination.FindPropertyRelative("b").vector2Value = source.B;
                destination.FindPropertyRelative("c").vector2Value = source.C;
            }

            serializedLayer.ApplyModifiedProperties();
            PrefabUtility.RecordPrefabInstancePropertyModifications(serializedLayer.targetObject);
            EditorUtility.SetDirty(serializedLayer.targetObject);
            state.hasPendingRegionSelection = false;
        }

        private void EnsureIslands(ViewState state)
        {
            if (state.mesh == state.cachedMesh &&
                state.subMeshIndex == state.cachedSubMeshIndex &&
                state.uvChannel == state.cachedUvChannel &&
                (state.islands != null || !string.IsNullOrEmpty(state.extractionError)))
            {
                return;
            }

            state.cachedMesh = state.mesh;
            state.cachedSubMeshIndex = state.subMeshIndex;
            state.cachedUvChannel = state.uvChannel;
            state.islands = null;
            state.extractionError = null;
            state.selectedIslandId = -1;
            if (state.mesh == null) return;

            using (MeshAnalysisMarker.Auto())
            {
                try
                {
                    state.islands = meshAnalysisCache.Get(state.mesh, state.subMeshIndex, state.uvChannel).GetIslands();
                }
                catch (Exception exception)
                {
                    state.extractionError = exception.Message;
                }
            }
        }

        private string GetMeshHash(Mesh mesh, int subMeshIndex, int uvChannel)
        {
            using (MeshAnalysisMarker.Auto())
            {
                return meshAnalysisCache.Get(mesh, subMeshIndex, uvChannel).GetContentHash();
            }
        }

        private static void Invalidate(ViewState state)
        {
            state.cachedMesh = null;
            state.cachedSubMeshIndex = -1;
            state.cachedUvChannel = -1;
            state.islands = null;
            state.extractionError = null;
            state.selectedIslandId = -1;
        }

        private void InitializeFromLayer()
        {
            serializedLayer = null;
            InvalidateAllCaches();
            if (layer == null)
            {
                cachedSourceModelOrPrefab = null;
                cachedTargetModelOrPrefab = null;
                cachedTargetTexture = null;
                activeRegionName = string.Empty;
                activeRegionIndex = -1;
                targetView.backgroundTexture = null;
                ClearMeshSelection(sourceView);
                ClearMeshSelection(targetView);
                return;
            }

            cachedSourceModelOrPrefab = null;
            cachedTargetModelOrPrefab = null;
            cachedTargetTexture = null;
            SynchronizeModelOrPrefabReferences();
            var currentSerializedLayer = GetSerializedLayer();
            currentSerializedLayer.UpdateIfRequiredOrScript();

            targetView.backgroundTexture = ResolveTargetTexture();

            ReconcileActiveRegion(currentSerializedLayer, true);
            regionReconcileRequest = RegionReconcileRequest.None;
        }

        private bool SynchronizeModelOrPrefabReferences()
        {
            var sourceRoot = layer == null ? null : layer.SourceModelOrPrefab;
            var targetRoot = layer == null ? null : layer.TargetModelOrPrefab;
            var targetTexture = ResolveTargetTexture();
            var changed = cachedSourceModelOrPrefab != sourceRoot ||
                          cachedTargetModelOrPrefab != targetRoot ||
                          cachedTargetTexture != targetTexture;
            if (!changed) return false;

            cachedSourceModelOrPrefab = sourceRoot;
            cachedTargetModelOrPrefab = targetRoot;
            cachedTargetTexture = targetTexture;
            InvalidateAllCaches();
            RequestRegionReconcile(RegionReconcileRequest.PreservePendingSelections);

            return true;
        }

        private SerializedObject GetSerializedLayer()
        {
            if (layer == null)
            {
                serializedLayer = null;
                return null;
            }

            if (serializedLayer == null || serializedLayer.targetObject != layer)
            {
                if (serializedLayer != null) InvalidateAllCaches();
                serializedLayer = new SerializedObject(layer);
                RequestRegionReconcile(RegionReconcileRequest.ReplacePendingSelections);
            }

            return serializedLayer;
        }

        private void InvalidateAllCaches()
        {
            meshAnalysisCache = new FBXUVMeshAnalysisCache();
            InvalidateCandidateCaches();
            Invalidate(sourceView);
            Invalidate(targetView);
        }

        private void InvalidateCandidateCaches()
        {
            candidateCache.Invalidate();
            regionNameLabelsValid = false;
            regionNameLabels = null;
        }

        private void RequestRegionReconcile(RegionReconcileRequest request)
        {
            if (request > regionReconcileRequest) regionReconcileRequest = request;
        }

        private RegionReconcileRequest ConsumeRegionReconcileRequest()
        {
            var request = regionReconcileRequest;
            regionReconcileRequest = RegionReconcileRequest.None;
            return request;
        }

        private bool ReconcileActiveRegion(
            SerializedObject serializedLayer,
            bool forceRefresh = false,
            bool preservePendingSelections = false)
        {
            var bindings = serializedLayer.FindProperty("regionBindings");
            if (bindings == null || !bindings.isArray || bindings.arraySize == 0)
            {
                var changed = activeRegionIndex != -1 || !string.IsNullOrEmpty(activeRegionName);
                activeRegionIndex = -1;
                activeRegionName = string.Empty;
                if (changed || forceRefresh)
                {
                    ClearMeshSelection(sourceView);
                    ClearMeshSelection(targetView);
                }

                return changed;
            }

            var reconciledIndex = -1;
            if (!string.IsNullOrEmpty(activeRegionName))
            {
                for (var index = 0; index < bindings.arraySize; index++)
                {
                    var candidateName = bindings.GetArrayElementAtIndex(index)
                        .FindPropertyRelative("name")?.stringValue;
                    if (NamesEqual(candidateName, activeRegionName))
                    {
                        reconciledIndex = index;
                        break;
                    }
                }
            }

            if (reconciledIndex < 0)
            {
                reconciledIndex = activeRegionIndex < 0
                    ? 0
                    : Mathf.Clamp(activeRegionIndex, 0, bindings.arraySize - 1);
            }

            var reconciledName = bindings.GetArrayElementAtIndex(reconciledIndex)
                .FindPropertyRelative("name")?.stringValue ?? string.Empty;
            var selectionChanged = activeRegionIndex != reconciledIndex ||
                                   !string.Equals(activeRegionName, reconciledName, StringComparison.Ordinal);
            activeRegionIndex = reconciledIndex;
            activeRegionName = reconciledName;
            if (selectionChanged)
            {
                RefreshSelectedIslandIds(serializedLayer);
            }
            else if (forceRefresh)
            {
                RefreshSelectedIslandIds(serializedLayer, preservePendingSelections);
            }

            return selectionChanged;
        }

        private SerializedProperty GetActiveBinding(SerializedObject serializedLayer)
        {
            var bindings = serializedLayer.FindProperty("regionBindings");
            return bindings != null && bindings.isArray &&
                   activeRegionIndex >= 0 && activeRegionIndex < bindings.arraySize
                ? bindings.GetArrayElementAtIndex(activeRegionIndex)
                : null;
        }

        private void RefreshSelectedIslandIds(
            SerializedObject serializedLayer,
            bool preservePendingSelections = false)
        {
            LoadViewFromRegion(
                serializedLayer,
                sourceView,
                Side.Source,
                true,
                preservePendingSelections);
            LoadViewFromRegion(
                serializedLayer,
                targetView,
                Side.Target,
                true,
                preservePendingSelections);
        }

        private void LoadViewFromRegion(
            SerializedObject serializedLayer,
            ViewState state,
            Side side,
            bool clearWhenMissing,
            bool preservePendingSelection = false)
        {
            if (preservePendingSelection && state.hasPendingRegionSelection) return;

            var binding = GetActiveBinding(serializedLayer);
            var region = binding?.FindPropertyRelative(side == Side.Source ? "sourceRegion" : "targetRegion");
            if (region == null)
            {
                if (clearWhenMissing) ClearMeshSelection(state);
                return;
            }

            var mesh = region.FindPropertyRelative("mesh").objectReferenceValue as Mesh;
            if (mesh == null)
            {
                if (clearWhenMissing) ClearMeshSelection(state);
                return;
            }

            state.mesh = mesh;
            state.subMeshIndex = region.FindPropertyRelative("subMeshIndex").intValue;
            state.uvChannel = region.FindPropertyRelative("uvChannel").intValue;
            state.hasPendingRegionSelection = false;
            Invalidate(state);
            EnsureIslands(state);
            if (!IsMeshHashCurrent(region, mesh, state.subMeshIndex, state.uvChannel))
            {
                state.selectedIslandId = -1;
                state.extractionError = "Mesh内容hashが一致しません。このregionのUV島を再選択してください。";
                return;
            }

            state.selectedIslandId = region.FindPropertyRelative("islandId").intValue;
        }

        private bool IsMeshHashCurrent(
            SerializedProperty region,
            Mesh mesh,
            int subMeshIndex,
            int uvChannel)
        {
            var storedHash = region.FindPropertyRelative("meshHash").stringValue;
            if (string.IsNullOrEmpty(storedHash)) return false;
            try
            {
                return string.Equals(
                    storedHash,
                    GetMeshHash(mesh, subMeshIndex, uvChannel),
                    StringComparison.Ordinal);
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static void ClearMeshSelection(ViewState state)
        {
            state.mesh = null;
            state.subMeshIndex = 0;
            state.uvChannel = 0;
            state.hasPendingRegionSelection = false;
            Invalidate(state);
        }

        private string[] CollectRegionNames(SerializedObject serializedLayer)
        {
            if (regionNameLabelsValid) return regionNameLabels;

            var bindings = serializedLayer.FindProperty("regionBindings");
            if (bindings == null || !bindings.isArray)
            {
                regionNameLabels = Array.Empty<string>();
                regionNameLabelsValid = true;
                return regionNameLabels;
            }

            regionNameLabels = new string[bindings.arraySize];
            for (var index = 0; index < bindings.arraySize; index++)
            {
                var name = bindings.GetArrayElementAtIndex(index)
                    .FindPropertyRelative("name")?.stringValue?.Trim();
                regionNameLabels[index] = string.IsNullOrEmpty(name) ? $"Region {index + 1}" : name;
            }

            regionNameLabelsValid = true;
            return regionNameLabels;
        }

        private List<FBXUVMeshOption> CollectSourceMeshes()
        {
            return candidateCache.GetSource(layer == null ? null : layer.SourceModelOrPrefab, out _).Options;
        }

        private List<FBXUVTargetMeshCandidate> CollectTargetMeshCandidates(Texture targetTexture)
        {
            return GetTargetCandidates(targetTexture).TargetCandidates;
        }

        private List<FBXUVMeshOption> CollectTargetMeshOptions(Texture targetTexture)
        {
            return GetTargetCandidates(targetTexture).Options;
        }

        private FBXUVMeshCandidateCache.Snapshot GetTargetCandidates(Texture targetTexture)
        {
            return candidateCache.GetTarget(layer, layer == null ? null : layer.TargetModelOrPrefab, targetTexture, out _);
        }

        private bool IsMeshSelectionAvailable(Side side, ViewState state)
        {
            if (side == Side.Source)
            {
                foreach (var item in CollectSourceMeshes())
                {
                    if (item.Mesh == state.mesh) return true;
                }

                return false;
            }

            foreach (var candidate in CollectTargetMeshCandidates(ResolveTargetTexture()))
            {
                if (candidate.Mesh != state.mesh) continue;
                foreach (var subMeshIndex in candidate.SubMeshIndices)
                {
                    if (subMeshIndex == state.subMeshIndex) return true;
                }
            }

            return false;
        }

        private static Vector3 UvToGui(Rect rect, Vector2 uv)
        {
            return new Vector3(
                Mathf.LerpUnclamped(rect.x, rect.xMax, uv.x),
                Mathf.LerpUnclamped(rect.yMax, rect.y, uv.y),
                0f);
        }

        private static T ObjectReference<T>(SerializedObject serializedObject, string propertyName)
            where T : UnityEngine.Object
        {
            return serializedObject.FindProperty(propertyName)?.objectReferenceValue as T;
        }

        private static bool NamesEqual(string first, string second)
        {
            return string.Equals(first?.Trim(), second?.Trim(), StringComparison.OrdinalIgnoreCase);
        }
    }
}
